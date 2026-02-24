using Axon.Core.Domain;
using Axon.Core.Ports;

namespace Axon.Infrastructure.Persistence.Decorators;

/// <summary>
/// HIPAA compliance decorator that wraps <see cref="IBiometricRepository"/>.
///
/// Intercepts every read and write operation and appends an immutable
/// <see cref="AuditLogEntry"/> to the <c>AuditLog</c> table before returning
/// to the caller. The audit write is durable (not fire-and-forget).
///
/// Decorator chain (outer → inner):
///   <see cref="AuditLoggingDecorator"/>
///     → <see cref="EncryptionDecorator"/>
///       → <see cref="BiometricRepository"/> (concrete)
///
/// Caller identity is resolved from ambient context (e.g. a thread-local
/// <c>IExecutionContext</c>) and passed down; this class does NOT access
/// the identity store directly, keeping it portable and testable.
/// </summary>
public sealed class AuditLoggingDecorator(
    IBiometricRepository inner,
    IAuditLogger         auditLogger,
    string               callerIdentity) : IBiometricRepository
{
    private const string RepoName = nameof(IBiometricRepository);

    // ── IRepository<BiometricEvent, Guid> ─────────────────────────────────────

    public async ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var result = await inner.GetByIdAsync(id, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Read, RepoName, callerIdentity,
            id.ToString(),
            result is null ? "GetById: not found" : "GetById: hit",
            ct).ConfigureAwait(false);

        return result;
    }

    public async ValueTask AddAsync(BiometricEvent evt, CancellationToken ct = default)
    {
        await inner.AddAsync(evt, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Write, RepoName, callerIdentity,
            evt.Id.ToString(),
            $"Add: Type={evt.Type}",
            ct).ConfigureAwait(false);
    }

    public async ValueTask AddRangeAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
    {
        await inner.AddRangeAsync(events, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Write, RepoName, callerIdentity,
            affectedEntityId: null,
            $"AddRange: count={events.Count}",
            ct).ConfigureAwait(false);
    }

    public async ValueTask UpdateAsync(BiometricEvent evt, CancellationToken ct = default)
    {
        await inner.UpdateAsync(evt, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Write, RepoName, callerIdentity,
            evt.Id.ToString(),
            $"Update: Type={evt.Type}",
            ct).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await inner.DeleteAsync(id, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Delete, RepoName, callerIdentity,
            id.ToString(),
            "Delete (GDPR wipe)",
            ct).ConfigureAwait(false);
    }

    // ── IBiometricRepository ──────────────────────────────────────────────────

    public async ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var result = await inner.QueryRangeAsync(type, from, to, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Read, RepoName, callerIdentity,
            affectedEntityId: null,
            $"QueryRange: Type={type} from={from:O} to={to:O} rows={result.Count}",
            ct).ConfigureAwait(false);

        return result;
    }

    // Streaming reads are audited once per call (not per yielded item)
    public async IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await auditLogger.LogAsync(
            AuditOperation.Read, RepoName, callerIdentity,
            affectedEntityId: null,
            $"StreamRange: Type={type} from={from:O} to={to:O}",
            ct).ConfigureAwait(false);

        await foreach (var evt in inner.StreamRangeAsync(type, from, to, ct).ConfigureAwait(false))
            yield return evt;
    }

    public async ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
        BiometricType type, DateTimeOffset from, DateTimeOffset to,
        int bucketSizeSeconds, CancellationToken ct = default)
    {
        var result = await inner
            .GetAggregatesAsync(type, from, to, bucketSizeSeconds, ct)
            .ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Read, RepoName, callerIdentity,
            affectedEntityId: null,
            $"GetAggregates: Type={type} buckets={result.Count}",
            ct).ConfigureAwait(false);

        return result;
    }

    public async ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
        CancellationToken ct = default)
    {
        var result = await inner.GetLatestVitalsAsync(ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Read, RepoName, callerIdentity,
            affectedEntityId: null,
            $"GetLatestVitals: types={result.Count}",
            ct).ConfigureAwait(false);

        return result;
    }

    public async ValueTask IngestBatchAsync(
        IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
    {
        await inner.IngestBatchAsync(events, ct).ConfigureAwait(false);

        await auditLogger.LogAsync(
            AuditOperation.Write, RepoName, callerIdentity,
            affectedEntityId: null,
            $"IngestBatch: count={events.Count}",
            ct).ConfigureAwait(false);
    }
}
