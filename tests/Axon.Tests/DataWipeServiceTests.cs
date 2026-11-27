using System.Security.Cryptography;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence;
using Axon.Infrastructure.Persistence.Decorators;
using Axon.Infrastructure.Security;
using Axon.UI.Application;
using Axon.UI.Logging;
using Axon.UI.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Axon.Tests;

/// <summary>
/// "Erase all data": every store the app writes is created with the app's own classes in a
/// temporary data folder laid out as App.BuildRuntime lays it out, next to and among files
/// that are not Axon's, then erased. Nothing here can reach the real %LOCALAPPDATA%\Axon.
/// </summary>
public sealed class DataWipeServiceTests : IDisposable
{
    private const string DatabaseErased = "Database: readings, tags, sync outbox and audit log";

    private readonly string _root;
    private readonly string _dataDir;
    private readonly List<string> _links = [];

    public DataWipeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "axon-wipe-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "Axon");
        Directory.CreateDirectory(_dataDir);

        // Next to the data folder, one of them sharing its name as a prefix.
        Write(Path.Combine(_root, "decoy.txt"), "not Axon's");
        Write(Path.Combine(_root, "AxonBackup", "notes.txt"), "not Axon's either");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var link in _links)
            TestLinks.Remove(link);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string DbPath => Path.Combine(_dataDir, AxonDbContextFactory.DbFileName);

    private string Marker => Path.Combine(_dataDir, AxonDataFolder.ErasePendingMarker);

    // ── Only Axon's files go ──────────────────────────────────────────────────

    [Fact]
    public async Task Wipe_ErasesEveryFileTheAppWrites_AndNothingElse()
    {
        // The person's own files, in and next to the data folder.
        var theirs = new[]
        {
            Path.Combine(_dataDir, "axon.vault.db.backup-2026-11-01"),
            Path.Combine(_dataDir, "my-exports", "report.csv"),
            Path.Combine(_dataDir, AxonDataFolder.LogsFolder, "my-notes.txt"),
        };
        foreach (var file in theirs) Write(file, "the person's own");

        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, journal);
        var stores = await CreateAppStoresAsync(vault);

        // The fixture really wrote each store.
        Assert.True(File.Exists(DbPath));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dataDir, AxonDataFolder.TokensFolder)));
        Assert.Contains(Directory.GetFiles(Path.Combine(_dataDir, AxonDataFolder.LogsFolder)), f => f.Contains("axon-", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(_dataDir, AxonDataFolder.HealthFolder, AxonDataFolder.HealthFileName)));
        if (OperatingSystem.IsWindows())
            Assert.Equal(3, Directory.GetFiles(Path.Combine(_dataDir, AxonDataFolder.KeysFolder)).Length);

        var result = await NewService(vault, stores.ReleaseSteps(journal)).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        // What is left is exactly the person's own files.
        Assert.Equal(
            theirs.Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFiles(_dataDir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "axon.vault.db.backup-2026-11-01", "my-exports" + Path.DirectorySeparatorChar, Path.Combine("logs", "my-notes.txt") }
                .Order(StringComparer.Ordinal),
            result.LeftInPlace.Order(StringComparer.Ordinal));
        Assert.Contains("Left in place (not created by Axon):", result.Describe());
        Assert.Equal(AxonDataFolder.KeyLabels.Order(), vault.Destroyed.Order());
        Assert.False(File.Exists(Marker));
        var expected = OperatingSystem.IsWindows()
            ? new[] { "Encryption keys", DatabaseErased, "Whoop sign-in (stored tokens)", "Logs", "Health report" }
            : [DatabaseErased, "Whoop sign-in (stored tokens)", "Logs", "Health report"];
        Assert.Equal(expected, result.Erased);
        AssertDecoysNextToTheFolderUntouched();
    }

    [Fact]
    public async Task Wipe_WithNothingButAxonsFiles_RemovesTheDataFolder()
    {
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, []);
        var stores = await CreateAppStoresAsync(vault);

        var result = await NewService(vault, stores.ReleaseSteps([])).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.Empty(result.LeftInPlace);
        Assert.False(Directory.Exists(_dataDir));
        AssertDecoysNextToTheFolderUntouched();
    }

    [Fact]
    public async Task Wipe_StopsReleasesMarksThenDestroysTheDatabaseKeyFirst_BeforeAnyFileIsDeleted()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, journal);
        var stores = await CreateAppStoresAsync(vault);

        await NewService(vault, stores.ReleaseSteps(journal)).WipeAsync();

        Assert.Equal(
        [
            "release field key (marker: False)",
            "release database (marker: False)",
            "release log file (marker: False)",
            .. AxonDataFolder.KeyLabels.Select(l => $"destroy {l} (database file: True, marker: True)"),
        ],
            journal);
        Assert.Equal(AxonDbContextFactory.KeyLabel, vault.Destroyed[0]);
    }

    [Fact]
    public async Task Wipe_HiddenAndReadOnlyAxonFiles_AreErasedToo()
    {
        var log = Path.Combine(_dataDir, AxonDataFolder.LogsFolder, "axon-20261127_001.log");
        Write(log, "{}");
        File.SetAttributes(log, FileAttributes.Hidden | FileAttributes.ReadOnly);

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.False(File.Exists(log));
        Assert.Contains("Logs", result.Erased);
    }

    [Fact]
    public async Task Wipe_WithTheDevelopmentVault_DoesNotClaimTheKeysWereDestroyed()
    {
        Write(DbPath, "db");

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.DoesNotContain("Encryption keys", result.Erased);
        Assert.Contains(result.Warnings, w => w.Contains("development key store", StringComparison.Ordinal));
    }

    // ── Failure and retry ─────────────────────────────────────────────────────

    [WindowsFact]
    public async Task Wipe_LockedFile_IsReportedTruthfully_AndASecondRunFinishes()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, journal);
        var stores = await CreateAppStoresAsync(vault);
        var service = NewService(vault, stores.ReleaseSteps(journal));
        var tokenFile = Path.Combine(_dataDir, AxonDataFolder.TokensFolder, "whoop.token");

        DataWipeResult first;
        using (new FileStream(tokenFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await service.WipeAsync();
        }

        Assert.Equal(DataWipeOutcome.Partial, first.Outcome);
        Assert.StartsWith(Path.Combine("tokens", "whoop.token") + " (", Assert.Single(first.NotErased));
        Assert.DoesNotContain("Whoop sign-in (stored tokens)", first.Erased);
        Assert.Contains(DatabaseErased, first.Erased);
        Assert.Contains("Not everything could be erased", first.Describe());
        Assert.False(File.Exists(DbPath));
        Assert.True(File.Exists(tokenFile));
        Assert.True(DataWipeService.IsPending(_dataDir));

        var second = await service.WipeAsync();

        Assert.True(second.Complete, second.Describe());
        Assert.False(Directory.Exists(_dataDir));
        Assert.False(DataWipeService.IsPending(_dataDir));
    }

    [Fact]
    public async Task Wipe_DatabaseKeyThatCannotBeDestroyed_ErasesNothing()
    {
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, []);
        vault.FailDestroy.Add(AxonDbContextFactory.KeyLabel);
        var stores = await CreateAppStoresAsync(vault);
        var tokens = Directory.GetFiles(Path.Combine(_dataDir, AxonDataFolder.TokensFolder));

        var result = await NewService(vault, stores.ReleaseSteps([])).WipeAsync();

        Assert.Equal(DataWipeOutcome.NothingErased, result.Outcome);
        Assert.Contains("Your data is still on this device", result.Describe());
        Assert.Empty(vault.Destroyed);
        Assert.True(File.Exists(DbPath));
        Assert.All(tokens, t => Assert.True(File.Exists(t)));
        Assert.False(DataWipeService.IsPending(_dataDir));
    }

    [Fact]
    public async Task Wipe_OtherKeyThatCannotBeDestroyed_IsReported_AndTheFilesStillGo()
    {
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, []);
        vault.FailDestroy.Add(EncryptionDecorator.EncKeyLabel);
        var stores = await CreateAppStoresAsync(vault);

        var result = await NewService(vault, stores.ReleaseSteps([])).WipeAsync();

        Assert.Equal(DataWipeOutcome.Partial, result.Outcome);
        Assert.Contains(result.NotErased, n => n.StartsWith($"Encryption key \"{EncryptionDecorator.EncKeyLabel}\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Encryption keys", result.Erased);
        Assert.False(File.Exists(DbPath));
        Assert.True(DataWipeService.IsPending(_dataDir));
    }

    [Fact]
    public async Task Wipe_RunTwice_TheSecondRunIsASafeNoOp()
    {
        var vault = new RecordingVault(InnerVault(), DbPath, Marker, []);
        var stores = await CreateAppStoresAsync(vault);
        var service = NewService(vault, stores.ReleaseSteps([]));

        var first = await service.WipeAsync();
        var second = await service.WipeAsync();

        Assert.True(first.Complete, first.Describe());
        Assert.True(second.Complete, second.Describe());
        Assert.False(Directory.Exists(_dataDir));
        AssertDecoysNextToTheFolderUntouched();
    }

    [Fact]
    public async Task Wipe_ReleaseStepThatFails_IsNoted_AndTheEraseCarriesOn()
    {
        Write(DbPath, "db");
        var failing = new WipeReleaseStep("Stopping the sync relay", () => throw new InvalidOperationException("relay stuck"));

        var result = await NewService(new MockHardwareVault(), [failing]).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.Contains("Stopping the sync relay failed: relay stuck", result.Warnings);
        Assert.False(Directory.Exists(_dataDir));
    }

    [Fact]
    public async Task Wipe_WhenThereIsNoDataFolder_IsComplete_AndCreatesNothing()
    {
        Directory.Delete(_dataDir);

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.False(Directory.Exists(_dataDir));
    }

    // ── Work still running ────────────────────────────────────────────────────

    [Fact]
    public async Task Wipe_WorkThatDoesNotStop_RefusesTheErase_WithNothingTouched()
    {
        Write(DbPath, "db");
        var activity = new VaultActivity();
        using var stubborn = activity.TryBegin("a Whoop sync")!;   // never ends on its own
        var vault = new RecordingVault(new MockHardwareVault(), DbPath, Marker, []);
        var released = false;

        var result = await new DataWipeService(_dataDir, vault,
            [new("Closing the database", () => { released = true; return ValueTask.CompletedTask; })],
            activity, quiesceBound: TimeSpan.FromMilliseconds(100)).WipeAsync();

        Assert.Equal(DataWipeOutcome.Refused, result.Outcome);
        Assert.Equal(["a Whoop sync"], result.NotErased);
        Assert.Contains("Nothing was erased, because this was still using your data: a Whoop sync", result.Describe());
        Assert.False(released);
        Assert.Empty(vault.Destroyed);
        Assert.True(File.Exists(DbPath));
        Assert.False(File.Exists(Marker));
        Assert.True(stubborn.Token.IsCancellationRequested);
        // The app carries on: new work is allowed again.
        using var next = activity.TryBegin("the dashboard load");
        Assert.NotNull(next);
    }

    [Fact]
    public async Task Wipe_StopsRunningWorkFirst_AndRefusesNewWork()
    {
        Write(DbPath, "db");
        var activity = new VaultActivity();
        var load = activity.TryBegin("the dashboard load")!;
        var running = Task.Run(async () =>
        {
            try { await Task.Delay(Timeout.Infinite, load.Token); }
            catch (OperationCanceledException) { }
            finally { load.Dispose(); }
        });

        var result = await new DataWipeService(_dataDir, new MockHardwareVault(), [], activity).WipeAsync();
        await running;

        Assert.True(result.Complete, result.Describe());
        Assert.Null(activity.TryBegin("a CSV import"));
    }

    // ── Links are never followed ──────────────────────────────────────────────

    [DirectoryLinkFact]
    public async Task Wipe_DataFolderThatIsALink_ErasesAxonsFilesInTheTarget_AndKeepsEverythingElse()
    {
        Directory.Delete(_dataDir);
        var target = Path.Combine(_root, "someone-elses-folder");
        Write(Path.Combine(target, "precious.txt"), "keep me");
        Write(Path.Combine(target, "sub", "precious2.txt"), "keep me too");
        Write(Path.Combine(target, AxonDbContextFactory.DbFileName), "db");
        Write(Path.Combine(target, AxonDataFolder.LogsFolder, "axon-20261127.log"), "{}");
        Assert.True(TestLinks.TryCreateDirectoryLink(_dataDir, target));
        _links.Add(_dataDir);

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.Equal("keep me", File.ReadAllText(Path.Combine(target, "precious.txt")));
        Assert.Equal("keep me too", File.ReadAllText(Path.Combine(target, "sub", "precious2.txt")));
        Assert.False(File.Exists(Path.Combine(target, AxonDbContextFactory.DbFileName)));
        Assert.False(File.Exists(Path.Combine(target, AxonDataFolder.LogsFolder, "axon-20261127.log")));
        Assert.True(new DirectoryInfo(_dataDir).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(Directory.Exists(target));
        Assert.Contains(result.Warnings, w => w.Contains("is a link to", StringComparison.Ordinal));
        Assert.Contains("precious.txt", result.LeftInPlace);
    }

    [DirectoryLinkFact]
    public async Task Wipe_DataFolderLinkedToAProtectedFolder_ErasesNothing()
    {
        Directory.Delete(_dataDir);
        Assert.True(TestLinks.TryCreateDirectoryLink(_dataDir, Path.GetTempPath()));
        _links.Add(_dataDir);
        var vault = new RecordingVault(new MockHardwareVault(), DbPath, Marker, []);

        var result = await NewService(vault, []).WipeAsync();

        Assert.Equal(DataWipeOutcome.NothingErased, result.Outcome);
        Assert.Empty(vault.Destroyed);
    }

    [DirectoryLinkFact]
    public async Task Wipe_LinkInsideTheDataFolder_IsLeftInPlace_AndNotFollowed()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Write(Path.Combine(outside, "precious.txt"), "keep me");
        Write(Path.Combine(outside, AxonDbContextFactory.DbFileName), "someone else's file with Axon's name");
        var link = Path.Combine(_dataDir, "link-to-elsewhere");
        Assert.True(TestLinks.TryCreateDirectoryLink(link, outside));
        _links.Add(link);
        Write(DbPath, "db");

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.Contains("link-to-elsewhere" + Path.DirectorySeparatorChar, result.LeftInPlace);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(outside, AxonDbContextFactory.DbFileName)));
        Assert.False(File.Exists(DbPath));
    }

    [DirectoryLinkFact]
    public async Task Wipe_AxonFolderNameThatIsALink_IsNotFollowed_AndTheEraseIsNotComplete()
    {
        var outside = Path.Combine(_root, "elsewhere-logs");
        var theirLog = Path.Combine(outside, "axon-20261127.log");
        Write(theirLog, "{}");
        var link = Path.Combine(_dataDir, AxonDataFolder.LogsFolder);
        Assert.True(TestLinks.TryCreateDirectoryLink(link, outside));
        _links.Add(link);

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.Equal(DataWipeOutcome.Partial, result.Outcome);
        Assert.Contains(result.NotErased, n => n.Contains("is a link", StringComparison.Ordinal));
        Assert.True(File.Exists(theirLog));
        Assert.True(DataWipeService.IsPending(_dataDir));
    }

    // ── The folder guard ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RefusesAFolderThatIsNotPlainlyTheAppsOwn()
    {
        var vault = new MockHardwareVault();
        var tempRoot = Path.GetPathRoot(Path.GetTempPath())!;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] refused =
        [
            "Axon",
            tempRoot,
            Path.Combine(tempRoot, "Axon"),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            profile,
            Path.GetDirectoryName(profile)!,
        ];

        foreach (var folder in refused)
            Assert.Throws<ArgumentException>(() => new DataWipeService(folder, vault, []));

        _ = new DataWipeService(_dataDir, vault, []);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private DataWipeService NewService(IHardwareVault vault, IReadOnlyList<WipeReleaseStep> steps) =>
        new(_dataDir, vault, steps, retryDelay: TimeSpan.FromMilliseconds(10));

    /// <summary>The DPAPI vault with its key files where the app keeps them; the mock elsewhere.</summary>
    private IHardwareVault InnerVault()
    {
        var keys = Path.Combine(_dataDir, AxonDataFolder.KeysFolder);
        Directory.CreateDirectory(keys);
        return OperatingSystem.IsWindows() ? new WindowsDataProtectionVault(keys) : new MockHardwareVault();
    }

    private void AssertDecoysNextToTheFolderUntouched()
    {
        Assert.Equal("not Axon's", File.ReadAllText(Path.Combine(_root, "decoy.txt")));
        Assert.Equal("not Axon's either", File.ReadAllText(Path.Combine(_root, "AxonBackup", "notes.txt")));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>Writes each store with the class the app uses for it, as App.BuildRuntime wires them.</summary>
    private async Task<AppStores> CreateAppStoresAsync(IHardwareVault vault)
    {
        var factory = new AxonDbContextFactory(vault);
        await factory.InitialiseAsync(_dataDir);
        var encrypted = new EncryptionDecorator(new BiometricRepository(factory), vault);
        var repository = new AuditLoggingDecorator(encrypted, new DbAuditLogger(factory), "test-user");
        var ts = new DateTimeOffset(2026, 6, 20, 7, 0, 0, TimeSpan.Zero);
        await repository.IngestBatchAsync(
        [
            new BiometricEvent(Guid.NewGuid(), ts, BiometricType.HeartRate, 61, "bpm",
                new SourceMetadata("whoop-4.0", "Whoop", null, 0.9f, ts)),
        ]);
        await repository.QueryRangeAsync(BiometricType.HeartRate, ts.AddHours(-1), ts.AddHours(1));
        await new TagRepository(factory).AddTagAsync(new Tag(Guid.NewGuid(), "caffeine", "stimulant"));

        await new EncryptedFileOAuthTokenStore(vault, Path.Combine(_dataDir, AxonDataFolder.TokensFolder)).SaveTokenAsync(
            "whoop", new OAuthTokenSet("access", "refresh", ts.AddHours(1), "read:recovery"));

        var logging = SerilogBootstrapper.CreateLoggerFactory(_dataDir);
        logging.CreateLogger("wipe-test").LogInformation("Sync finished for {Count} events", 1);

        await new JsonHealthReportWriter(_dataDir).WriteAsync(
            new RelaySnapshot(RelayState.Idle, 1, null, null, AirGapEnabled: false, "Loopback Relay"));

        return new AppStores(factory, encrypted, logging, Marker);
    }

    private sealed record AppStores(
        AxonDbContextFactory Factory, EncryptionDecorator Encrypted, ILoggerFactory Logging, string Marker)
    {
        /// <summary>The app's release steps, minus the relay (not built here), each noted in the journal.</summary>
        public IReadOnlyList<WipeReleaseStep> ReleaseSteps(List<string> journal) =>
        [
            new("Clearing the field-encryption key from memory", async () =>
            {
                journal.Add($"release field key (marker: {File.Exists(Marker)})");
                await Encrypted.DisposeAsync();
            }),
            new("Closing the database", () =>
            {
                journal.Add($"release database (marker: {File.Exists(Marker)})");
                Factory.Close();
                return ValueTask.CompletedTask;
            }),
            new("Closing the log file", () =>
            {
                journal.Add($"release log file (marker: {File.Exists(Marker)})");
                Logging.Dispose();
                return ValueTask.CompletedTask;
            }),
        ];
    }

    /// <summary>Wraps the real vault; notes each key destruction and what was on disk at that moment.</summary>
    private sealed class RecordingVault(IHardwareVault inner, string dbPath, string marker, List<string> journal) : IHardwareVault
    {
        public HashSet<string> FailDestroy { get; } = [];

        public List<string> Destroyed { get; } = [];

        public bool IsHardwareBacked => inner.IsHardwareBacked;

        public ValueTask<Memory<byte>> DeriveKeyAsync(string keyLabel, CancellationToken ct = default) =>
            inner.DeriveKeyAsync(keyLabel, ct);

        public void ZeroKey(Memory<byte> keyMaterial) => inner.ZeroKey(keyMaterial);

        public async ValueTask DestroyKeyAsync(string keyLabel, CancellationToken ct = default)
        {
            journal.Add($"destroy {keyLabel} (database file: {File.Exists(dbPath)}, marker: {File.Exists(marker)})");
            if (FailDestroy.Contains(keyLabel))
                throw new CryptographicException("the key store is unavailable");
            await inner.DestroyKeyAsync(keyLabel, ct);
            Destroyed.Add(keyLabel);
        }
    }
}
