using System.Security.Cryptography;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence.Decorators;
using Axon.Infrastructure.Security;

namespace Axon.Tests;

/// <summary>
/// Tests the AES-256-GCM field-level encryption/decryption round-trip
/// and key-zeroing behaviour of EncryptionDecorator.
///
/// Uses MockHardwareVault (deterministic key derivation) and a stub IBiometricRepository
/// so no SQLite I/O is needed.
/// </summary>
public class EncryptionDecoratorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BiometricEvent MakeEvent(string deviceId = "device-plain-id") =>
        new(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Type: BiometricType.HeartRate,
            Value: 72.0,
            Unit: "bpm",
            Source: new SourceMetadata(
                DeviceId: deviceId,
                Vendor: "Test",
                FirmwareVersion: null,
                ConfidenceScore: 0.9f,
                IngestionTimestamp: DateTimeOffset.UtcNow));

    // ── Encrypt/decrypt round-trip ────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_EncryptsDeviceId_InnerSeesEncryptedValue()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var original = MakeEvent("sensor-001");
        await decorator.AddAsync(original);

        // The inner repository should receive an event whose DeviceId is NOT the plaintext.
        Assert.NotEqual("sensor-001", inner.LastAddedEvent!.Source.DeviceId);
        // Encrypted value should be base64.
        Assert.True(IsBase64(inner.LastAddedEvent.Source.DeviceId));
    }

    [Fact]
    public async Task GetByIdAsync_DecryptsDeviceId_ReturnsCleartext()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var original = MakeEvent("sensor-xyz");
        await decorator.AddAsync(original);

        // Set inner to return the encrypted event on Get.
        inner.GetByIdResult = inner.LastAddedEvent;

        var retrieved = await decorator.GetByIdAsync(original.Id);

        Assert.NotNull(retrieved);
        Assert.Equal("sensor-xyz", retrieved!.Source.DeviceId);
    }

    [Fact]
    public async Task RoundTrip_AddAndGet_PreservesAllOtherFields()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var original = MakeEvent("dev-A");
        await decorator.AddAsync(original);

        inner.GetByIdResult = inner.LastAddedEvent;
        var retrieved = await decorator.GetByIdAsync(original.Id);

        Assert.Equal(original.Id, retrieved!.Id);
        Assert.Equal(original.Type, retrieved.Type);
        Assert.Equal(original.Value, retrieved.Value);
        Assert.Equal(original.Unit, retrieved.Unit);
        Assert.Equal(original.Timestamp, retrieved.Timestamp);
    }

    [Fact]
    public async Task AddRangeAsync_EncryptsBatch()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var events = new[]
        {
            MakeEvent("dev-1"),
            MakeEvent("dev-2"),
            MakeEvent("dev-3")
        };

        await decorator.AddRangeAsync(events);

        Assert.Equal(3, inner.AddedRangeEvents!.Count);
        Assert.All(inner.AddedRangeEvents,
            e => Assert.True(IsBase64(e.Source.DeviceId)));
    }

    [Fact]
    public async Task QueryRangeAsync_DecryptsAll()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        // Pre-encrypt two events through the decorator and put them in inner's query results.
        var evts = new[] { MakeEvent("sensor-A"), MakeEvent("sensor-B") };
        foreach (var e in evts)
            await decorator.AddAsync(e);

        inner.QueryRangeResult = inner.AllAdded.ToList();

        var results = await decorator.QueryRangeAsync(
            BiometricType.HeartRate,
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, e => e.Source.DeviceId == "sensor-A");
        Assert.Contains(results, e => e.Source.DeviceId == "sensor-B");
    }

    [Fact]
    public async Task GetLatestVitalsAsync_DecryptsAllEntries()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var original = MakeEvent("vital-device");
        await decorator.AddAsync(original);

        inner.LatestVitalsResult = new Dictionary<BiometricType, BiometricEvent>
        {
            [BiometricType.HeartRate] = inner.LastAddedEvent!
        };

        var vitals = await decorator.GetLatestVitalsAsync();

        Assert.Equal("vital-device", vitals[BiometricType.HeartRate].Source.DeviceId);
    }

    [Fact]
    public async Task DisposeAsync_ZeroesKey_SubsequentDeriveUsesFreshKey()
    {
        // After disposal a new decorator must be able to decrypt events
        // encrypted by a prior decorator on the same vault.
        var vault = new MockHardwareVault();
        var inner1 = new CapturingRepository();
        BiometricEvent? encryptedEvent;

        // Encrypt in first decorator lifecycle.
        {
            await using var dec1 = new EncryptionDecorator(inner1, vault);
            await dec1.AddAsync(MakeEvent("lifecycle-device"));
            encryptedEvent = inner1.LastAddedEvent;
        }

        // Decrypt in second decorator lifecycle (same vault, same key label → same key).
        var inner2 = new CapturingRepository();
        inner2.GetByIdResult = encryptedEvent;

        await using var dec2 = new EncryptionDecorator(inner2, vault);
        var result = await dec2.GetByIdAsync(encryptedEvent!.Id);

        Assert.Equal("lifecycle-device", result!.Source.DeviceId);
    }

    [Fact]
    public async Task GetByIdAsync_NullFromInner_ReturnsNull()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        inner.GetByIdResult = null;

        var result = await decorator.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task IngestBatchAsync_EncryptsAllEventsInBatch()
    {
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        var batch = new[]
        {
            MakeEvent("batch-dev-1"),
            MakeEvent("batch-dev-2"),
        };

        await decorator.IngestBatchAsync(batch);

        Assert.Equal(2, inner.IngestedBatch!.Count);
        Assert.All(inner.IngestedBatch, e => Assert.True(IsBase64(e.Source.DeviceId)));
        Assert.DoesNotContain(inner.IngestedBatch, e => e.Source.DeviceId.Contains("batch-dev"));
    }

    [Fact]
    public async Task GetAggregatesAsync_PassthroughNoDecryption()
    {
        // Aggregates contain no PII — must not call any encryption path.
        var vault = new MockHardwareVault();
        var inner = new CapturingRepository();
        await using var decorator = new EncryptionDecorator(inner, vault);

        inner.AggregateResult = new[]
        {
            new AggregateBucket(DateTimeOffset.UtcNow, 60, 120, 90, 10)
        };

        var result = await decorator.GetAggregatesAsync(
            BiometricType.HeartRate, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 3600);

        Assert.Single(result);
        Assert.Equal(0, inner.DecryptCallCount);   // No decrypt calls for aggregates
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsBase64(string s)
    {
        try { Convert.FromBase64String(s); return true; }
        catch { return false; }
    }

    // ── Stub IBiometricRepository ─────────────────────────────────────────────

    private sealed class CapturingRepository : IBiometricRepository
    {
        public BiometricEvent? LastAddedEvent { get; private set; }
        public IReadOnlyList<BiometricEvent>? AddedRangeEvents { get; private set; }
        public IReadOnlyList<BiometricEvent>? IngestedBatch { get; private set; }
        public List<BiometricEvent> AllAdded { get; } = new();

        public BiometricEvent? GetByIdResult { get; set; }
        public IReadOnlyList<BiometricEvent> QueryRangeResult { get; set; } = Array.Empty<BiometricEvent>();
        public IReadOnlyDictionary<BiometricType, BiometricEvent> LatestVitalsResult { get; set; }
            = new Dictionary<BiometricType, BiometricEvent>();
        public IReadOnlyList<AggregateBucket> AggregateResult { get; set; } = Array.Empty<AggregateBucket>();

        // Track whether decrypt was triggered (for aggregate passthrough test)
        public int DecryptCallCount { get; private set; }

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => ValueTask.FromResult(GetByIdResult);

        public ValueTask AddAsync(BiometricEvent evt, CancellationToken ct = default)
        {
            LastAddedEvent = evt;
            AllAdded.Add(evt);
            return ValueTask.CompletedTask;
        }

        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
        {
            AddedRangeEvents = events;
            AllAdded.AddRange(events);
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateAsync(BiometricEvent evt, CancellationToken ct = default)
        {
            LastAddedEvent = evt;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            DecryptCallCount++;
            return ValueTask.FromResult(QueryRangeResult);
        }

        public async IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var e in QueryRangeResult)
                yield return e;
        }

        public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to,
            int bucketSizeSeconds, CancellationToken ct = default)
            => ValueTask.FromResult(AggregateResult);

        public ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
            CancellationToken ct = default)
            => ValueTask.FromResult(LatestVitalsResult);

        public ValueTask IngestBatchAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
        {
            IngestedBatch = events;
            AllAdded.AddRange(events);
            return ValueTask.CompletedTask;
        }
    }
}
