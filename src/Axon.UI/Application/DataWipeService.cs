using System.Text;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Decorators;
using Axon.Infrastructure.Security;

namespace Axon.UI.Application;

/// <summary>
/// Something that has to let go of the data before it can be erased: a background loop,
/// an open database or log file, a key cached in memory.
/// </summary>
internal sealed record WipeReleaseStep(string Description, Func<ValueTask> ReleaseAsync);

/// <summary>What an erase removed, and what is still on this device.</summary>
/// <param name="Erased">Kinds of data that are gone.</param>
/// <param name="NotErased">Every key or file that is still there, with the reason.</param>
/// <param name="Warnings">Steps that failed without leaving data behind.</param>
internal sealed record DataWipeResult(
    IReadOnlyList<string> Erased,
    IReadOnlyList<string> NotErased,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True only when nothing Axon stored remains.</summary>
    public bool Complete => NotErased.Count == 0;

    /// <summary>Plain-language summary for the person, listing what was and was not erased.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        if (Complete)
        {
            text.Append("Everything Axon stored on this device has been erased.");
        }
        else
        {
            text.Append("Not everything could be erased. Still on this device:");
            AppendList(text, NotErased);
        }

        if (Erased.Count > 0)
        {
            text.Append(Complete ? "\nErased:" : "\n\nErased:");
            AppendList(text, Erased);
        }

        if (!Complete)
            text.Append("\n\nClose any program that is using these files, then erase again.");

        if (Warnings.Count > 0)
        {
            text.Append("\n\nNotes:");
            AppendList(text, Warnings);
        }

        return text.ToString();
    }

    private static void AppendList(StringBuilder text, IReadOnlyList<string> items)
    {
        foreach (var item in items)
            text.Append("\n• ").Append(item);
    }
}

/// <summary>
/// Erases everything Axon stores about the person on this device: the "Erase all data"
/// action in Settings.
///
/// Everything Axon writes lives in one data folder (<c>%LOCALAPPDATA%\Axon</c> on Windows):
///   • <c>axon.vault.db</c> (+ <c>-wal</c>, <c>-shm</c>, <c>-journal</c>): the SQLCipher database
///     with the readings, tags and annotations, the sync outbox and the audit log;
///   • <c>keys\*.key</c>: the DPAPI-protected keys for the database, the field encryption and the
///     token files (on platforms whose vault is the OS keychain, the keys live there instead);
///   • <c>tokens\*.token</c>: the encrypted OAuth tokens (Whoop today);
///   • <c>logs\axon-*.log</c>: the rolling application log;
///   • <c>health\status.json</c>: the relay health report.
///
/// Order: mark the erase as started, run the release steps (stop the relay, drop the cached
/// field key, close the database and its connection pool, close the log file), destroy the
/// keys through the vault, then delete every file in the data folder (the database first),
/// then the folder. Destroying the keys first means a database or token file that cannot be
/// deleted is left encrypted under a key that no longer exists. Logs and the health report
/// are not encrypted, so they are only gone once deleted.
///
/// Failure: a locked file is retried a few times, then reported. Nothing is reported as
/// erased that is still there, and running the erase again finishes the job. The marker
/// file stays until nothing is left, so an erase that was cut short is finished at the next
/// start before anything opens the data (see <see cref="IsPending"/>).
///
/// Nothing outside the data folder is touched: links inside it are removed, never followed.
/// </summary>
internal sealed class DataWipeService
{
    /// <summary>Name of the marker file that says an erase has started but not finished.</summary>
    internal const string PendingMarkerName = "erase-pending";

    /// <summary>Every vault key Axon derives.</summary>
    internal static readonly IReadOnlyList<string> AppKeyLabels =
    [
        AxonDbContextFactory.KeyLabel,
        EncryptionDecorator.EncKeyLabel,
        EncryptedFileOAuthTokenStore.TokenKeyLabel,
    ];

    private const int DeleteAttempts = 4;

    private const string DatabaseCategory = "Database: readings, tags, sync outbox and audit log";
    private const string KeysCategory = "Encryption keys";
    private const string TokensCategory = "Whoop sign-in (stored tokens)";
    private const string LogsCategory = "Logs";
    private const string HealthCategory = "Health report";
    private const string OtherCategory = "Other files in the data folder";

    private static readonly string[] CategoryOrder =
        [KeysCategory, DatabaseCategory, TokensCategory, LogsCategory, HealthCategory, OtherCategory];

    private readonly string _dataDirectory;
    private readonly IHardwareVault _vault;
    private readonly IReadOnlyList<WipeReleaseStep> _releaseSteps;
    private readonly IReadOnlyList<string> _keyLabels;
    private readonly TimeSpan _retryDelay;

    public DataWipeService(
        string dataDirectory,
        IHardwareVault vault,
        IReadOnlyList<WipeReleaseStep> releaseSteps,
        IReadOnlyList<string>? keyLabels = null,
        TimeSpan? retryDelay = null)
    {
        _dataDirectory = EnsureOwnFolder(dataDirectory);
        _vault = vault;
        _releaseSteps = releaseSteps;
        _keyLabels = keyLabels ?? AppKeyLabels;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>True when an erase started in <paramref name="dataDirectory"/> has not finished.</summary>
    public static bool IsPending(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, PendingMarkerName));

    private string MarkerPath => Path.Combine(_dataDirectory, PendingMarkerName);

    /// <summary>
    /// Runs the erase. Not cancellable: once the keys are gone, stopping halfway would only
    /// leave more behind. Safe to run again after a partial result, or when nothing is left.
    /// </summary>
    public async Task<DataWipeResult> WipeAsync()
    {
        var notErased = new List<string>();
        var warnings = new List<string>();

        // 1. Record that an erase is under way, so one that cannot finish now is finished
        //    at the next start, before the vault is opened again.
        if (Directory.Exists(_dataDirectory))
        {
            try
            {
                await File.WriteAllBytesAsync(MarkerPath, []).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not record that an erase is under way: {ex.Message}");
            }
        }

        // 2. Make everything that uses the data let go of it.
        foreach (var step in _releaseSteps)
        {
            try
            {
                await step.ReleaseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                warnings.Add($"{step.Description} failed: {ex.Message}");
            }
        }

        // 3. Destroy the keys before deleting any file.
        bool keysDestroyed = true;
        foreach (var label in _keyLabels)
        {
            try
            {
                await _vault.DestroyKeyAsync(label).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                keysDestroyed = false;
                notErased.Add($"Encryption key \"{label}\": {ex.Message}");
            }
        }

        // 4. Delete every file in the data folder, the database first.
        var found = new HashSet<string>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (leaves, directories) = Walk(notErased);
        foreach (var leaf in leaves.OrderBy(DeletePriority))
        {
            var category = CategoryOf(leaf.FullName);
            found.Add(category);
            var error = await TryDeleteAsync(leaf).ConfigureAwait(false);
            if (error is not null)
            {
                failed.Add(category);
                failedPaths.Add(leaf.FullName);
                notErased.Add($"{RelativePath(leaf.FullName)} ({error})");
            }
        }

        // 5. Check nothing came back (a write that was still in flight), then remove the
        //    folders, the marker and the data folder itself.
        var (stragglers, _) = Walk([]);
        foreach (var leaf in stragglers.Where(l => !failedPaths.Contains(l.FullName)))
        {
            found.Add(CategoryOf(leaf.FullName));
            failed.Add(CategoryOf(leaf.FullName));
            notErased.Add($"{RelativePath(leaf.FullName)} (written while the erase ran)");
        }

        foreach (var directory in directories)
            TryDeleteEmptyDirectory(directory, warnings);

        if (notErased.Count == 0 && Directory.Exists(_dataDirectory))
        {
            try
            {
                File.Delete(MarkerPath);
                Directory.Delete(_dataDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"The empty data folder could not be removed: {ex.Message}");
            }
        }
        else if (notErased.Count > 0 && Directory.Exists(_dataDirectory) && !File.Exists(MarkerPath))
        {
            // Step 1 could not write it; without it the next start would not finish the erase.
            try
            {
                await File.WriteAllBytesAsync(MarkerPath, []).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not record that the erase is unfinished: {ex.Message}");
            }
        }

        var erased = CategoryOrder
            .Where(c => c == KeysCategory
                ? keysDestroyed && !failed.Contains(KeysCategory)
                : found.Contains(c) && !failed.Contains(c))
            .ToList();

        return new DataWipeResult(erased, notErased, warnings);
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every file in the data folder (hidden and system files included) and every
    /// sub-folder, children before parents. A link is listed as a leaf and never entered,
    /// so deleting it removes the link and not what it points to.
    /// </summary>
    private (List<FileSystemInfo> Leaves, List<DirectoryInfo> Directories) Walk(List<string> notErased)
    {
        var leaves = new List<FileSystemInfo>();
        var directories = new List<DirectoryInfo>();
        if (!Directory.Exists(_dataDirectory))
            return (leaves, directories);

        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
        };

        void Visit(DirectoryInfo folder)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                notErased.Add($"{RelativePath(folder.FullName)} (its contents could not be listed: {ex.Message})");
                return;
            }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    Visit(sub);
                    directories.Add(sub);
                }
                else if (!IsMarker(entry.FullName))
                {
                    leaves.Add(entry);
                }
            }
        }

        Visit(new DirectoryInfo(_dataDirectory));
        return (leaves, directories);
    }

    /// <summary>Deletes one file or link, retrying while it is locked. Returns why it failed, or null.</summary>
    private async Task<string?> TryDeleteAsync(FileSystemInfo leaf)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                leaf.Refresh();
                if (!leaf.Exists)
                    return null;
                if (leaf.Attributes.HasFlag(FileAttributes.ReadOnly))
                    leaf.Attributes &= ~FileAttributes.ReadOnly;
                leaf.Delete();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                    return ex.Message;
            }

            await Task.Delay(_retryDelay).ConfigureAwait(false);
        }
    }

    private static void TryDeleteEmptyDirectory(DirectoryInfo directory, List<string> warnings)
    {
        try
        {
            directory.Refresh();
            if (directory.Exists && !directory.EnumerateFileSystemInfos().Any())
                directory.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"An empty folder could not be removed: {directory.Name} ({ex.Message})");
        }
    }

    private bool IsMarker(string path) =>
        string.Equals(path, MarkerPath, StringComparison.OrdinalIgnoreCase);

    private int DeletePriority(FileSystemInfo leaf) => CategoryOf(leaf.FullName) switch
    {
        DatabaseCategory => 0,
        TokensCategory => 1,
        KeysCategory => 2,
        _ => 3,
    };

    private string CategoryOf(string path)
    {
        var relative = RelativePath(path);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separator < 0)
        {
            return relative.StartsWith(AxonDbContextFactory.DbFileName, StringComparison.OrdinalIgnoreCase)
                ? DatabaseCategory
                : OtherCategory;
        }

        return relative[..separator].ToLowerInvariant() switch
        {
            "keys" => KeysCategory,
            "tokens" => TokensCategory,
            "logs" => LogsCategory,
            "health" => HealthCategory,
            _ => OtherCategory,
        };
    }

    private string RelativePath(string path) => Path.GetRelativePath(_dataDirectory, path);

    /// <summary>
    /// The erase deletes the whole folder, so refuse anything that is not plainly an app's
    /// own folder: a relative path, a drive root, or a folder that is or holds the user's
    /// profile, app-data, documents, desktop or temp folder.
    /// </summary>
    private static string EnsureOwnFolder(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("The data folder must be an absolute path.", nameof(dataDirectory));

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)
            || string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Refusing to erase a drive root.", nameof(dataDirectory));
        }

        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory,
        ];
        var protectedPaths = protectedFolders
            .Select(Environment.GetFolderPath)
            .Append(Path.GetTempPath())
            .Where(p => p.Length > 0)
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)));

        foreach (var path in protectedPaths)
        {
            if (string.Equals(path, full, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Refusing to erase '{full}': it is or contains a system or user folder.", nameof(dataDirectory));
            }
        }

        return full;
    }
}
