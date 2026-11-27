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
/// temporary data folder laid out as App.BuildRuntime lays it out, then erased.
/// </summary>
public sealed class DataWipeServiceTests : IDisposable
{
    private const string DatabaseErased = "Database: readings, tags, sync outbox and audit log";

    private readonly string _root;
    private readonly string _dataDir;
    private readonly string _decoyFile;
    private readonly string _decoyFolderFile;

    public DataWipeServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "axon-wipe-" + Guid.NewGuid().ToString("N"));
        _dataDir = Path.Combine(_root, "Axon");
        Directory.CreateDirectory(_dataDir);

        // Next to the data folder, one of them sharing its name as a prefix.
        _decoyFile = Path.Combine(_root, "decoy.txt");
        File.WriteAllText(_decoyFile, "not Axon's");
        _decoyFolderFile = Path.Combine(_root, "AxonBackup", "notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(_decoyFolderFile)!);
        File.WriteAllText(_decoyFolderFile, "not Axon's either");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private string DbPath => Path.Combine(_dataDir, AxonDbContextFactory.DbFileName);

    private string TokensDir => Path.Combine(_dataDir, "tokens");

    // ── Everything goes ───────────────────────────────────────────────────────

    [Fact]
    public async Task Wipe_ErasesEveryStoreTheAppCreates()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, journal);
        var stores = await CreateAppStoresAsync(vault);

        // The fixture really wrote each store.
        Assert.True(File.Exists(DbPath));
        Assert.NotEmpty(Directory.GetFiles(TokensDir));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_dataDir, "logs")));
        Assert.True(File.Exists(Path.Combine(_dataDir, "health", "status.json")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(3, Directory.GetFiles(Path.Combine(_dataDir, "keys")).Length);

        var result = await NewService(vault, stores.ReleaseSteps(journal)).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.False(Directory.Exists(_dataDir));
        Assert.Equal(
            DataWipeService.AppKeyLabels.Order(),
            vault.Destroyed.Order());
        Assert.Equal(
            ["Encryption keys", DatabaseErased, "Whoop sign-in (stored tokens)", "Logs", "Health report"],
            result.Erased);
        Assert.Empty(result.Warnings);
        AssertDecoysUntouched();
    }

    [Fact]
    public async Task Wipe_LetsGoOfTheData_ThenDestroysTheKeys_BeforeAnyFileIsDeleted()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, journal);
        var stores = await CreateAppStoresAsync(vault);

        await NewService(vault, stores.ReleaseSteps(journal)).WipeAsync();

        Assert.Equal(
        [
            "release field key",
            "release database",
            "release log file",
            .. DataWipeService.AppKeyLabels.Select(l => $"destroy {l} while the database file exists: True"),
        ],
            journal);
    }

    [Fact]
    public async Task Wipe_HiddenAndReadOnlyFilesInTheDataFolder_AreErasedToo()
    {
        var hidden = Path.Combine(_dataDir, "logs", "axon-20261127.log");
        Directory.CreateDirectory(Path.GetDirectoryName(hidden)!);
        File.WriteAllText(hidden, "{}");
        File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);
        var stray = Path.Combine(_dataDir, "unknown-cache.bin");
        File.WriteAllText(stray, "x");

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.False(Directory.Exists(_dataDir));
        Assert.Contains("Other files in the data folder", result.Erased);
        AssertDecoysUntouched();
    }

    // ── Failure and retry ─────────────────────────────────────────────────────

    [Fact]
    public async Task Wipe_LockedFile_IsReportedTruthfully_AndASecondRunFinishes()
    {
        if (!OperatingSystem.IsWindows()) return;   // an open handle only blocks a delete on Windows

        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, journal);
        var stores = await CreateAppStoresAsync(vault);
        var service = NewService(vault, stores.ReleaseSteps(journal));
        var tokenFile = Path.Combine(TokensDir, "whoop.token");

        DataWipeResult first;
        using (new FileStream(tokenFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            first = await service.WipeAsync();
        }

        Assert.False(first.Complete);
        Assert.StartsWith(Path.Combine("tokens", "whoop.token") + " (", Assert.Single(first.NotErased));
        Assert.DoesNotContain("Whoop sign-in (stored tokens)", first.Erased);
        Assert.Contains(DatabaseErased, first.Erased);
        Assert.Contains("Encryption keys", first.Erased);
        Assert.Contains("Not everything could be erased", first.Describe());
        Assert.False(File.Exists(DbPath));
        Assert.True(File.Exists(tokenFile));
        Assert.True(DataWipeService.IsPending(_dataDir));

        var second = await service.WipeAsync();

        Assert.True(second.Complete, second.Describe());
        Assert.False(Directory.Exists(_dataDir));
        Assert.False(DataWipeService.IsPending(_dataDir));
        AssertDecoysUntouched();
    }

    [Fact]
    public async Task Wipe_KeyThatCannotBeDestroyed_IsReported_AndTheFilesStillGo()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, journal);
        vault.FailDestroy.Add(EncryptionDecorator.EncKeyLabel);
        var stores = await CreateAppStoresAsync(vault);

        var result = await NewService(vault, stores.ReleaseSteps(journal)).WipeAsync();

        Assert.False(result.Complete);
        Assert.Contains(result.NotErased, n => n.StartsWith($"Encryption key \"{EncryptionDecorator.EncKeyLabel}\"", StringComparison.Ordinal));
        Assert.DoesNotContain("Encryption keys", result.Erased);
        Assert.False(File.Exists(DbPath));
        Assert.True(DataWipeService.IsPending(_dataDir));
    }

    [Fact]
    public async Task Wipe_RunTwice_TheSecondRunIsASafeNoOp()
    {
        var journal = new List<string>();
        var vault = new RecordingVault(InnerVault(), DbPath, journal);
        var stores = await CreateAppStoresAsync(vault);
        var service = NewService(vault, stores.ReleaseSteps(journal));

        var first = await service.WipeAsync();
        var second = await service.WipeAsync();

        Assert.True(first.Complete, first.Describe());
        Assert.True(second.Complete, second.Describe());
        Assert.True(second.Warnings.Count == 0, second.Describe());
        Assert.False(Directory.Exists(_dataDir));
        AssertDecoysUntouched();
    }

    [Fact]
    public async Task Wipe_ReleaseStepThatFails_IsNoted_AndTheEraseCarriesOn()
    {
        File.WriteAllText(Path.Combine(_dataDir, AxonDbContextFactory.DbFileName), "db");
        var failing = new WipeReleaseStep("Stopping the sync relay", () => throw new InvalidOperationException("relay stuck"));

        var result = await NewService(new MockHardwareVault(), [failing]).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.Equal("Stopping the sync relay failed: relay stuck", Assert.Single(result.Warnings));
        Assert.False(Directory.Exists(_dataDir));
    }

    [Fact]
    public async Task Wipe_WhenThereIsNoDataFolder_IsComplete_AndCreatesNothing()
    {
        Directory.Delete(_dataDir);

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete);
        Assert.False(Directory.Exists(_dataDir));
    }

    // ── Nothing outside the data folder ───────────────────────────────────────

    [Fact]
    public async Task Wipe_RemovesALinkInsideTheDataFolder_NotWhatItPointsTo()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "keep me");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_dataDir, "link-to-elsewhere"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // this account may not create symbolic links (Windows without Developer Mode)
        }

        var result = await NewService(new MockHardwareVault(), []).WipeAsync();

        Assert.True(result.Complete, result.Describe());
        Assert.False(Directory.Exists(_dataDir));
        Assert.True(File.Exists(precious));
        AssertDecoysUntouched();
    }

    [Fact]
    public void Constructor_RefusesAFolderThatIsNotPlainlyTheAppsOwn()
    {
        var vault = new MockHardwareVault();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] refused =
        [
            "Axon",
            Path.GetPathRoot(Path.GetTempPath())!,
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            profile,
            Path.GetDirectoryName(profile)!,
        ];

        foreach (var folder in refused)
            Assert.Throws<ArgumentException>(() => new DataWipeService(folder, vault, []));

        // The folder the app actually uses is accepted.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (appData.Length > 0)
            _ = new DataWipeService(Path.Combine(appData, "Axon"), vault, []);
    }

    // ── Fixture ───────────────────────────────────────────────────────────────

    private DataWipeService NewService(IHardwareVault vault, IReadOnlyList<WipeReleaseStep> steps) =>
        new(_dataDir, vault, steps, retryDelay: TimeSpan.FromMilliseconds(10));

    /// <summary>The DPAPI vault with its key files where the app keeps them; the mock elsewhere.</summary>
    private IHardwareVault InnerVault()
    {
        var keys = Path.Combine(_dataDir, "keys");
        Directory.CreateDirectory(keys);
        return OperatingSystem.IsWindows() ? new WindowsDataProtectionVault(keys) : new MockHardwareVault();
    }

    private void AssertDecoysUntouched()
    {
        Assert.Equal("not Axon's", File.ReadAllText(_decoyFile));
        Assert.Equal("not Axon's either", File.ReadAllText(_decoyFolderFile));
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

        await new EncryptedFileOAuthTokenStore(vault, TokensDir).SaveTokenAsync(
            "whoop", new OAuthTokenSet("access", "refresh", ts.AddHours(1), "read:recovery"));

        var logging = SerilogBootstrapper.CreateLoggerFactory(_dataDir);
        logging.CreateLogger("wipe-test").LogInformation("Sync finished for {Count} events", 1);

        await new JsonHealthReportWriter(_dataDir).WriteAsync(
            new RelaySnapshot(RelayState.Idle, 1, null, null, AirGapEnabled: false, "Loopback Relay"));

        return new AppStores(factory, encrypted, logging);
    }

    private sealed record AppStores(AxonDbContextFactory Factory, EncryptionDecorator Encrypted, ILoggerFactory Logging)
    {
        /// <summary>The app's release steps, minus the relay (not built here), each noted in the journal.</summary>
        public IReadOnlyList<WipeReleaseStep> ReleaseSteps(List<string> journal) =>
        [
            new("Clearing the field-encryption key from memory", async () =>
            {
                journal.Add("release field key");
                await Encrypted.DisposeAsync();
            }),
            new("Closing the database", () =>
            {
                journal.Add("release database");
                Factory.Close();
                return ValueTask.CompletedTask;
            }),
            new("Closing the log file", () =>
            {
                journal.Add("release log file");
                Logging.Dispose();
                return ValueTask.CompletedTask;
            }),
        ];
    }

    /// <summary>Wraps the real vault; notes each key destruction and whether the database was still there.</summary>
    private sealed class RecordingVault(IHardwareVault inner, string dbPath, List<string> journal) : IHardwareVault
    {
        public HashSet<string> FailDestroy { get; } = [];

        public List<string> Destroyed { get; } = [];

        public bool IsHardwareBacked => inner.IsHardwareBacked;

        public ValueTask<Memory<byte>> DeriveKeyAsync(string keyLabel, CancellationToken ct = default) =>
            inner.DeriveKeyAsync(keyLabel, ct);

        public void ZeroKey(Memory<byte> keyMaterial) => inner.ZeroKey(keyMaterial);

        public async ValueTask DestroyKeyAsync(string keyLabel, CancellationToken ct = default)
        {
            journal.Add($"destroy {keyLabel} while the database file exists: {File.Exists(dbPath)}");
            if (FailDestroy.Contains(keyLabel))
                throw new CryptographicException("the key store is unavailable");
            await inner.DestroyKeyAsync(keyLabel, ct);
            Destroyed.Add(keyLabel);
        }
    }
}
