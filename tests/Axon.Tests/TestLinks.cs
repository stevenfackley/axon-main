using System.Diagnostics;

namespace Axon.Tests;

/// <summary>
/// Directory links for the erase tests: a junction on Windows (no privilege needed), a
/// directory symbolic link elsewhere.
/// </summary>
internal static class TestLinks
{
    private static readonly Lazy<bool> Probe = new(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "axon-linkprobe-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            return TryCreateDirectoryLink(link, target);
        }
        finally
        {
            Remove(link);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    });

    public static bool CanCreateDirectoryLinks => Probe.Value;

    public static bool TryCreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes the link itself (never what it points to).</summary>
    public static void Remove(string link)
    {
        try
        {
            if (new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(link);
        }
        catch
        {
            // best-effort
        }
    }
}

/// <summary>Runs where a directory link can be made; elsewhere it is reported as skipped, never as passed.</summary>
public sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        if (!TestLinks.CanCreateDirectoryLinks)
            Skip = "This account cannot create a directory link here.";
    }
}

/// <summary>Runs on Windows only (an open handle blocks a delete only there); skipped elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "An open file blocks its deletion only on Windows.";
    }
}
