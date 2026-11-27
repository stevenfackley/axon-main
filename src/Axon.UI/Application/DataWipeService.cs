using System.Text;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Security;

namespace Axon.UI.Application;

/// <summary>
/// Something that has to let go of the data before it can be erased: a background loop,
/// an open database or log file, a key cached in memory.
/// </summary>
internal sealed record WipeReleaseStep(string Description, Func<ValueTask> ReleaseAsync);

internal enum DataWipeOutcome
{
    /// <summary>Every file Axon wrote is gone and the keys are destroyed.</summary>
    Complete,

    /// <summary>Some of Axon's files or keys are still there; erasing again finishes the job.</summary>
    Partial,

    /// <summary>Work on the data was still running, so nothing was touched; the app carries on.</summary>
    Refused,

    /// <summary>The erase could not start (the data folder failed its check); nothing was stopped
    /// or touched, and the app carries on.</summary>
    NotStarted,

    /// <summary>The database key could not be destroyed and still unprotects, so nothing was
    /// deleted; the data can still be opened.</summary>
    NothingErased,
}

/// <summary>What an erase removed, what is still on this device, and what it left alone.</summary>
/// <param name="Erased">Kinds of Axon data that are gone.</param>
/// <param name="NotErased">Every key or Axon file still there, with the reason (for
/// <see cref="DataWipeOutcome.Refused"/>: the work that was still running).</param>
/// <param name="LeftInPlace">Entries in the data folder that Axon did not create.</param>
/// <param name="Warnings">Steps that failed without leaving Axon data behind.</param>
internal sealed record DataWipeResult(
    DataWipeOutcome Outcome,
    IReadOnlyList<string> Erased,
    IReadOnlyList<string> NotErased,
    IReadOnlyList<string> LeftInPlace,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True only when nothing Axon stored remains.</summary>
    public bool Complete => Outcome == DataWipeOutcome.Complete;

    /// <summary>Plain-language summary for the person: what was and was not erased, and what was left alone.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        switch (Outcome)
        {
            case DataWipeOutcome.Refused:
                text.Append("Nothing was erased, because this was still using your data: ")
                    .Append(string.Join(", ", NotErased))
                    .Append(". Wait for it to finish, then erase again.");
                return text.ToString();

            case DataWipeOutcome.NotStarted:
                text.Append("Nothing was erased: ").Append(string.Join("; ", NotErased)).Append('.');
                return text.ToString();

            case DataWipeOutcome.NothingErased:
                text.Append("Nothing was erased:");
                AppendList(text, NotErased);
                text.Append("\n\nYour data is still on this device and can still be opened. " +
                            "Restart Axon to keep using it, or erase again.");
                break;

            case DataWipeOutcome.Complete:
                text.Append("Everything Axon stored on this device has been erased.");
                AppendSection(text, "Erased:", Erased);
                break;

            default:
                text.Append("Not everything could be erased. Still on this device:");
                AppendList(text, NotErased);
                AppendSection(text, "Erased:", Erased);
                text.Append("\n\nClose any program that is using these files, then erase again.");
                break;
        }

        AppendSection(text, "Left in place (not created by Axon):", LeftInPlace);
        AppendSection(text, "Notes:", Warnings);
        return text.ToString();
    }

    private static void AppendSection(StringBuilder text, string heading, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        text.Append("\n\n").Append(heading);
        AppendList(text, items);
    }

    private static void AppendList(StringBuilder text, IReadOnlyList<string> items)
    {
        foreach (var item in items)
            text.Append("\n• ").Append(item);
    }
}

/// <summary>
/// Erases what Axon stores about the person on this device: the "Erase all data" action in
/// Settings. It deletes only the files Axon itself writes, by name (see
/// <see cref="AxonDataFolder"/>); anything else in the folder is left and reported.
///
/// Order (after checking where the files are, see the link rule below):
///   1. Stop the work that uses the data (<see cref="VaultActivity.QuiesceAsync"/>), or
///      refuse with nothing touched if it does not stop in time.
///   2. Release steps: stop the relay, drop the cached field key, close the database and its
///      connection pool, close the log file. Nothing is deleted yet.
///   3. Write the erase-pending marker.
///   4. Destroy the database key. If that fails, the vault is asked whether the key still
///      unprotects: if it does, stop (nothing is deleted, the marker is removed, the data can
///      still be opened); if the store cannot be read, stop too and keep the marker; if the
///      key no longer works (a zeroed blob whose delete failed), the data is already
///      unreadable and the erase goes on. So while the database key works, no file has been
///      deleted (start-up relies on this). Then destroy the other keys.
///   5. Delete Axon's files: the database first, then tokens, key blobs, logs, health report.
///      A locked file is retried, then reported; running the erase again finishes the job.
///   6. Check again, remove Axon's emptied sub-folders, and remove the marker last, only when
///      none of Axon's files is left. The data folder itself goes only if it is a plain
///      folder and now empty.
///
/// Links are never followed: a listed name that is a link (or any reparse point) is left in
/// place and reported. If the data folder itself is a link (a relocated folder, say a junction
/// to D:\AxonData), its final target must not be a drive or share root, or a user or system
/// folder; Axon's files inside the target are deleted and the link and the target folder stay.
/// </summary>
internal sealed class DataWipeService
{
    private const int DeleteAttempts = 4;

    /// <summary>A data folder must sit at least this many folders below its drive root.</summary>
    private const int MinimumFolderDepth = 2;

    private const string DatabaseCategory = "Database: readings, tags, sync outbox and audit log";
    private const string KeysCategory = "Encryption keys";
    private const string TokensCategory = "Whoop sign-in (stored tokens)";
    private const string LogsCategory = "Logs";
    private const string HealthCategory = "Health report";

    private static readonly string[] CategoryOrder =
        [KeysCategory, DatabaseCategory, TokensCategory, LogsCategory, HealthCategory];

    private readonly string _dataDirectory;
    private readonly IHardwareVault _vault;
    private readonly IReadOnlyList<WipeReleaseStep> _releaseSteps;
    private readonly VaultActivity? _activity;
    private readonly TimeSpan _quiesceBound;
    private readonly TimeSpan _retryDelay;

    public DataWipeService(
        string dataDirectory,
        IHardwareVault vault,
        IReadOnlyList<WipeReleaseStep> releaseSteps,
        VaultActivity? activity = null,
        TimeSpan? quiesceBound = null,
        TimeSpan? retryDelay = null)
    {
        _dataDirectory = EnsureOwnFolder(dataDirectory);
        _vault = vault;
        _releaseSteps = releaseSteps;
        _activity = activity;
        _quiesceBound = quiesceBound ?? TimeSpan.FromSeconds(5);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>The data folder this service erases.</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>True when an erase started in <paramref name="dataDirectory"/> has not finished.</summary>
    public static bool IsPending(string dataDirectory) =>
        File.Exists(Path.Combine(dataDirectory, AxonDataFolder.ErasePendingMarker));

    /// <summary>
    /// Forgets an unfinished erase the person chose not to finish: removes the marker and
    /// nothing else. Returns why that failed, or null.
    /// </summary>
    public string? CancelPending()
    {
        try
        {
            File.Delete(Path.Combine(ResolveRoot(out _), AxonDataFolder.ErasePendingMarker));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Runs the erase. Not cancellable: once the keys are gone, stopping halfway would only
    /// leave more behind. Safe to run again after a partial result, or when nothing is left.
    /// </summary>
    public async Task<DataWipeResult> WipeAsync()
    {
        var notErased = new List<string>();
        var warnings = new List<string>();

        // Where the files are, checked before anything is stopped or touched.
        string root;
        bool rootIsLink;
        try
        {
            root = ResolveRoot(out rootIsLink);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new DataWipeResult(DataWipeOutcome.NotStarted, [],
                [$"the data folder {_dataDirectory} is a link that cannot be followed safely ({ex.Message})"], [], warnings);
        }

        // 1. Stop the work that uses the data. Nothing has been touched if this refuses.
        if (_activity is not null)
        {
            var running = await _activity.QuiesceAsync(_quiesceBound).ConfigureAwait(false);
            if (running.Count > 0)
                return new DataWipeResult(DataWipeOutcome.Refused, [], running, [], []);
        }

        // 2. Let go of the data.
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

        if (rootIsLink)
            warnings.Add($"The data folder is a link to {root}. Axon's files there were erased; the link and that folder were left in place.");

        var marker = Path.Combine(root, AxonDataFolder.ErasePendingMarker);

        // 3. Record that an erase is under way, so start-up knows about one that is cut short.
        if (Directory.Exists(root))
            await TryWriteMarkerAsync(marker, warnings).ConfigureAwait(false);

        // 4. The database key first. While it still works, nothing is deleted.
        bool keysDestroyed = true;
        try
        {
            await _vault.DestroyKeyAsync(AxonDbContextFactory.KeyLabel).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed destroy may still have zeroed the key (the DPAPI vault overwrites the
            // blob before it deletes it), so ask whether it still works before saying anything.
            var state = _vault is IVaultKeyInspector inspector
                ? inspector.GetKeyState(AxonDbContextFactory.KeyLabel)
                : VaultKeyState.Usable;
            switch (state)
            {
                case VaultKeyState.Usable:
                    TryDelete(marker);
                    return new DataWipeResult(DataWipeOutcome.NothingErased, [],
                        [$"the database encryption key could not be destroyed ({ex.Message})"], [], warnings);

                case VaultKeyState.Unknown:
                    return new DataWipeResult(DataWipeOutcome.Partial, [],
                        [$"the database encryption key could not be destroyed ({ex.Message}), and whether it " +
                         "still works cannot be checked, so nothing else was erased"], [], warnings);

                default:
                    // It no longer works (zeroed, delete failed): the database cannot be opened,
                    // so the erase goes on. The leftover key file is one of Axon's files: the
                    // steps below delete it or report it.
                    break;
            }
        }

        foreach (var label in AxonDataFolder.KeyLabels.Where(l => l != AxonDbContextFactory.KeyLabel))
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

        // 5. Delete Axon's own files, the database first.
        var before = Collect(root);
        var found = new HashSet<string>(before.AxonFiles.Select(f => f.Category), StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var failedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, category) in before.AxonFiles.OrderBy(f => f.Category == DatabaseCategory ? 0 : 1))
        {
            var error = await TryDeleteAsync(file).ConfigureAwait(false);
            if (error is null) continue;
            failed.Add(category);
            failedPaths.Add(file.FullName);
            notErased.Add($"{Relative(root, file.FullName)} ({error})");
        }

        // 6. Look again: anything of Axon's still there (written while the erase ran, behind
        //    a link, or unreadable) keeps the erase unfinished.
        var after = Collect(root);
        foreach (var (file, category) in after.AxonFiles.Where(f => !failedPaths.Contains(f.File.FullName)))
        {
            failed.Add(category);
            notErased.Add($"{Relative(root, file.FullName)} (written while the erase ran)");
        }

        foreach (var (path, category) in after.Links)
        {
            failed.Add(category);
            notErased.Add($"{path} is a link or other reparse point, which Axon does not create; " +
                          "it and what it points to were left in place");
        }

        notErased.AddRange(after.Unreadable);

        foreach (var folder in new[] { AxonDataFolder.TokensFolder, AxonDataFolder.KeysFolder, AxonDataFolder.LogsFolder, AxonDataFolder.HealthFolder })
            TryRemoveEmptyFolder(Path.Combine(root, folder), warnings);

        // The marker goes last, and only when nothing of Axon's is left.
        if (notErased.Count == 0)
        {
            var markerError = File.Exists(marker) ? TryDelete(marker) : null;
            if (markerError is not null)
                notErased.Add($"{AxonDataFolder.ErasePendingMarker} ({markerError})");
            else if (!rootIsLink)
                TryRemoveEmptyFolder(root, warnings);
        }
        else if (Directory.Exists(root) && !File.Exists(marker))
        {
            await TryWriteMarkerAsync(marker, warnings).ConfigureAwait(false);
        }

        if (!_vault.IsHardwareBacked)
        {
            warnings.Add("This build uses the development key store, whose keys are derived from fixed " +
                         "names, so there were no stored keys to destroy.");
        }

        var erased = CategoryOrder
            .Where(c => c == KeysCategory
                ? _vault.IsHardwareBacked && keysDestroyed && !failed.Contains(KeysCategory)
                : found.Contains(c) && !failed.Contains(c))
            .ToList();

        var outcome = notErased.Count == 0 ? DataWipeOutcome.Complete : DataWipeOutcome.Partial;
        return new DataWipeResult(outcome, erased, notErased, after.Unlisted, warnings);
    }

    // ── What is in the folder ─────────────────────────────────────────────────

    private sealed class Inventory
    {
        public List<(FileInfo File, string Category)> AxonFiles { get; } = [];

        /// <summary>Listed names that are links: never entered, never deleted.</summary>
        public List<(string Path, string Category)> Links { get; } = [];

        /// <summary>Entries Axon did not create (folders are listed as a whole).</summary>
        public List<string> Unlisted { get; } = [];

        public List<string> Unreadable { get; } = [];
    }

    /// <summary>
    /// Sorts the top of the data folder, and the inside of Axon's own sub-folders, into
    /// Axon's files, links where Axon's names should be, and entries that are not Axon's.
    /// Does not enter any other folder and never follows a link.
    /// </summary>
    private static Inventory Collect(string root)
    {
        var inventory = new Inventory();
        if (!Directory.Exists(root))
            return inventory;

        (string Name, Func<string, bool> IsAxonsFile, string Category)[] ownFolders =
        [
            (AxonDataFolder.TokensFolder, AxonDataFolder.IsTokenFile, TokensCategory),
            (AxonDataFolder.KeysFolder, n => AxonDataFolder.KeyFiles.Contains(n, StringComparer.OrdinalIgnoreCase), KeysCategory),
            (AxonDataFolder.LogsFolder, AxonDataFolder.IsLogFile, LogsCategory),
            (AxonDataFolder.HealthFolder, n => n.Equals(AxonDataFolder.HealthFileName, StringComparison.OrdinalIgnoreCase), HealthCategory),
        ];

        foreach (var entry in List(root, root, inventory))
        {
            var name = entry.Name;
            if (name.Equals(AxonDataFolder.ErasePendingMarker, StringComparison.OrdinalIgnoreCase) && IsPlainFile(entry))
                continue;

            if (AxonDataFolder.DatabaseFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                Sort(root, entry, DatabaseCategory, inventory);
                continue;
            }

            var own = ownFolders.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (own.Name is null)
            {
                inventory.Unlisted.Add(Relative(root, entry.FullName, entry));
            }
            else if (IsLink(entry))
            {
                inventory.Links.Add((Relative(root, entry.FullName, entry), own.Category));
            }
            else if (entry is not DirectoryInfo)
            {
                inventory.Unlisted.Add(Relative(root, entry.FullName, entry));
            }
            else
            {
                foreach (var child in List(entry.FullName, root, inventory))
                {
                    if (own.IsAxonsFile(child.Name))
                        Sort(root, child, own.Category, inventory);
                    else
                        inventory.Unlisted.Add(Relative(root, child.FullName, child));
                }
            }
        }

        return inventory;
    }

    /// <summary>An entry with one of Axon's names: a plain file is Axon's; anything else is not.</summary>
    private static void Sort(string root, FileSystemInfo entry, string category, Inventory inventory)
    {
        if (IsLink(entry))
            inventory.Links.Add((Relative(root, entry.FullName, entry), category));
        else if (entry is FileInfo file)
            inventory.AxonFiles.Add((file, category));
        else
            inventory.Unlisted.Add(Relative(root, entry.FullName, entry));
    }

    private static List<FileSystemInfo> List(string folder, string root, Inventory inventory)
    {
        try
        {
            return new DirectoryInfo(folder)
                .EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.None,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            inventory.Unreadable.Add($"{Relative(root, folder)} (its contents could not be listed: {ex.Message})");
            return [];
        }
    }

    /// <summary>Symbolic links and junctions are reparse points; so is anything else Axon did not write plainly.</summary>
    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsPlainFile(FileSystemInfo entry) => entry is FileInfo && !IsLink(entry);

    // ── Deleting ──────────────────────────────────────────────────────────────

    /// <summary>Deletes one plain file, retrying while it is locked. Returns why it failed, or null.</summary>
    private async Task<string?> TryDeleteAsync(FileInfo file)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                file.Refresh();
                if (!file.Exists)
                    return null;
                if (file.Attributes.HasFlag(FileAttributes.ReadOnly))
                    file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
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

    private static string? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    private static async Task TryWriteMarkerAsync(string marker, List<string> warnings)
    {
        try
        {
            await File.WriteAllBytesAsync(marker, []).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not record that an erase is under way: {ex.Message}");
        }
    }

    /// <summary>Removes a plain folder that is now empty; leaves links and non-empty folders.</summary>
    private static void TryRemoveEmptyFolder(string path, List<string> warnings)
    {
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists || IsLink(folder) || folder.EnumerateFileSystemInfos().Any())
                return;
            folder.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"An empty folder could not be removed: {path} ({ex.Message})");
        }
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The folder the files are in: the data folder, or the final target when the data
    /// folder is a link, which must pass the same guard.
    /// </summary>
    private string ResolveRoot(out bool isLink)
    {
        var folder = new DirectoryInfo(_dataDirectory);
        isLink = folder.Exists && IsLink(folder);
        if (!isLink)
            return _dataDirectory;

        var target = folder.ResolveLinkTarget(returnFinalTarget: true)
            ?? throw new ArgumentException("it is a reparse point that does not resolve to a folder.");
        return EnsureSafeLinkTarget(target.FullName);
    }

    private static string Relative(string root, string path, FileSystemInfo? entry = null)
    {
        var relative = Path.GetRelativePath(root, path);
        return entry is DirectoryInfo ? relative + Path.DirectorySeparatorChar : relative;
    }

    /// <summary>
    /// Refuse anything that is not plainly an app's own folder: a relative path, a drive
    /// root or a folder just below one, or a folder that is or holds the user's profile,
    /// app-data, documents, desktop or temp folder. This is for the configured path, where a
    /// short path means a misconfiguration (an empty app-data path turning into ".\Axon").
    /// </summary>
    private static string EnsureOwnFolder(string dataDirectory)
    {
        var full = EnsureSafeLinkTarget(dataDirectory);
        var root = Path.GetPathRoot(full)!;
        if (full[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries).Length < MinimumFolderDepth)
        {
            throw new ArgumentException($"Refusing to erase '{full}': it is too close to a drive root.", nameof(dataDirectory));
        }

        return full;
    }

    /// <summary>
    /// The check for where a relocated data folder points (a junction to D:\AxonData is
    /// fine): an absolute path that is not a drive or share root, and not a folder that is or
    /// holds the user's profile, app-data, documents, desktop or temp folder. Only Axon's own
    /// names are deleted there.
    /// </summary>
    internal static string EnsureSafeLinkTarget(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("The data folder must be an absolute path.", nameof(dataDirectory));

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)
            || string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Refusing to erase '{full}': it is a drive or share root.", nameof(dataDirectory));
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
