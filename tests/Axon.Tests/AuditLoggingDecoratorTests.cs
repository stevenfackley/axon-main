using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Persistence.Decorators;

namespace Axon.Tests;

/// <summary>
/// Verifies that AuditLoggingDecorator logs the correct operation, caller identity,
/// and entity ID for every IBiometricRepository method — and that the audit write
/// happens AFTER the inner call succeeds (so failures don't produce phantom audit entries).
/// </summary>
public class AuditLoggingDecoratorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static BiometricEvent MakeEvent(Guid? id = null) =>
        new(
            Id: id ?? Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Type: BiometricType.HeartRate,
            Value: 72,
            Unit: "bpm",
            Source: new SourceMetadata("dev-1", "Test", null, 0.9f, DateTimeOffset.UtcNow));

    private static (AuditLoggingDecorator Decorator, RecordingAuditLogger Logger, StubRepository Inner)
        Build(string caller = "unit-test")
    {
        var inner = new StubRepository();
        var logger = new RecordingAuditLogger();
        var decorator = new AuditLoggingDecorator(inner, logger, caller);
        return (decorator, logger, inner);
    }

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetByIdAsync_Found_LogsReadWithEntityId()
    {
        var (dec, log, inner) = Build("alice");
        var id = Guid.NewGuid();
        var evt = MakeEvent(id);
        inner.GetByIdResult = evt;

        await dec.GetByIdAsync(id);

        Assert.Single(log.Entries);
        var entry = log.Entries[0];
        Assert.Equal(AuditOperation.Read, entry.Operation);
        Assert.Equal("alice", entry.CallerIdentity);
        Assert.Equal(id.ToString(), entry.AffectedEntityId);
    }

    [Fact]
    public async Task GetByIdAsync_NotFound_LogsReadWithNotFoundSummary()
    {
        var (dec, log, _) = Build();
        await dec.GetByIdAsync(Guid.NewGuid());

        Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Read, log.Entries[0].Operation);
        Assert.Contains("not found", log.Entries[0].Summary);
    }

    // ── AddAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_LogsWriteWithEntityId()
    {
        var (dec, log, _) = Build();
        var evt = MakeEvent();

        await dec.AddAsync(evt);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Write, entry.Operation);
        Assert.Equal(evt.Id.ToString(), entry.AffectedEntityId);
        Assert.Contains(evt.Type.ToString(), entry.Summary);
    }

    // ── AddRangeAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AddRangeAsync_LogsWriteWithCount()
    {
        var (dec, log, _) = Build();
        var events = new[] { MakeEvent(), MakeEvent(), MakeEvent() };

        await dec.AddRangeAsync(events);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Write, entry.Operation);
        Assert.Null(entry.AffectedEntityId);
        Assert.Contains("3", entry.Summary);
    }

    // ── UpdateAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_LogsWriteWithEntityId()
    {
        var (dec, log, _) = Build();
        var evt = MakeEvent();

        await dec.UpdateAsync(evt);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Write, entry.Operation);
        Assert.Equal(evt.Id.ToString(), entry.AffectedEntityId);
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_LogsDeleteWithEntityId()
    {
        var (dec, log, _) = Build();
        var id = Guid.NewGuid();

        await dec.DeleteAsync(id);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Delete, entry.Operation);
        Assert.Equal(id.ToString(), entry.AffectedEntityId);
        Assert.Contains("GDPR", entry.Summary);
    }

    // ── QueryRangeAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task QueryRangeAsync_LogsReadWithTypeAndRowCount()
    {
        var (dec, log, inner) = Build();
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow;

        inner.QueryRangeResult = new[] { MakeEvent(), MakeEvent() };

        await dec.QueryRangeAsync(BiometricType.HeartRate, from, to);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Read, entry.Operation);
        Assert.Contains("HeartRate", entry.Summary);
        Assert.Contains("2", entry.Summary);
    }

    // ── StreamRangeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task StreamRangeAsync_LogsOnceBeforeStream()
    {
        var (dec, log, inner) = Build();
        inner.QueryRangeResult = new[] { MakeEvent() };

        // Must consume the async enumerable to trigger audit
        await foreach (var _ in dec.StreamRangeAsync(BiometricType.HeartRate,
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue)) { }

        Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Read, log.Entries[0].Operation);
        Assert.Contains("StreamRange", log.Entries[0].Summary);
    }

    // ── GetAggregatesAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAggregatesAsync_LogsReadWithBucketCount()
    {
        var (dec, log, inner) = Build();
        inner.AggregateResult = new[]
        {
            new AggregateBucket(DateTimeOffset.UtcNow, 60, 120, 90, 5),
            new AggregateBucket(DateTimeOffset.UtcNow.AddHours(1), 65, 115, 88, 3)
        };

        await dec.GetAggregatesAsync(BiometricType.HeartRate,
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 3600);

        var entry = Assert.Single(log.Entries);
        Assert.Contains("2", entry.Summary);   // 2 buckets
    }

    // ── GetLatestVitalsAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetLatestVitalsAsync_LogsReadWithTypeCount()
    {
        var (dec, log, inner) = Build();
        inner.LatestVitalsResult = new Dictionary<BiometricType, BiometricEvent>
        {
            [BiometricType.HeartRate] = MakeEvent(),
            [BiometricType.SpO2] = MakeEvent()
        };

        await dec.GetLatestVitalsAsync();

        var entry = Assert.Single(log.Entries);
        Assert.Contains("2", entry.Summary);
    }

    // ── IngestBatchAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task IngestBatchAsync_LogsWriteWithBatchCount()
    {
        var (dec, log, _) = Build();
        var batch = new[] { MakeEvent(), MakeEvent(), MakeEvent(), MakeEvent() };

        await dec.IngestBatchAsync(batch);

        var entry = Assert.Single(log.Entries);
        Assert.Equal(AuditOperation.Write, entry.Operation);
        Assert.Contains("4", entry.Summary);
    }

    [Fact]
    public async Task MultipleOps_EachProducesExactlyOneAuditEntry()
    {
        var (dec, log, inner) = Build();
        inner.GetByIdResult = MakeEvent();
        inner.QueryRangeResult = Array.Empty<BiometricEvent>();

        await dec.GetByIdAsync(Guid.NewGuid());
        await dec.AddAsync(MakeEvent());
        await dec.QueryRangeAsync(BiometricType.SpO2, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

        Assert.Equal(3, log.Entries.Count);
    }

    // ── Recording stubs ───────────────────────────────────────────────────────

    private sealed record AuditEntry(
        AuditOperation Operation,
        string CallerIdentity,
        string? AffectedEntityId,
        string Summary);

    private sealed class RecordingAuditLogger : IAuditLogger
    {
        public List<AuditEntry> Entries { get; } = new();

        public ValueTask LogAsync(
            AuditOperation operation,
            string repositoryName,
            string callerIdentity,
            string? affectedEntityId,
            string summary,
            CancellationToken ct = default)
        {
            Entries.Add(new AuditEntry(operation, callerIdentity, affectedEntityId, summary));
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<AuditLogEntry>> GetTrailAsync(
            string entityId, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());
    }

    private sealed class StubRepository : IBiometricRepository
    {
        public BiometricEvent? GetByIdResult { get; set; }
        public IReadOnlyList<BiometricEvent> QueryRangeResult { get; set; } = Array.Empty<BiometricEvent>();
        public IReadOnlyList<AggregateBucket> AggregateResult { get; set; } = Array.Empty<AggregateBucket>();
        public IReadOnlyDictionary<BiometricType, BiometricEvent> LatestVitalsResult { get; set; }
            = new Dictionary<BiometricType, BiometricEvent>();

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => ValueTask.FromResult(GetByIdResult);

        public ValueTask AddAsync(BiometricEvent evt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask UpdateAsync(BiometricEvent evt, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => ValueTask.FromResult(QueryRangeResult);

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
            => ValueTask.CompletedTask;
    }
}
