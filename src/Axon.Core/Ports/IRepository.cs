using Axon.Core.Domain;

namespace Axon.Core.Ports;

/// <summary>
/// Generic repository port.
/// All concrete implementations (and their decorators) must satisfy this contract.
/// ValueTask is mandated for all high-frequency paths to minimise Task allocation overhead.
/// </summary>
/// <typeparam name="TEntity">Aggregate root stored in this repository.</typeparam>
/// <typeparam name="TId">Strongly-typed identifier (Guid, long, etc.).</typeparam>
public interface IRepository<TEntity, TId>
    where TEntity : class
{
    /// <summary>Fetch a single entity by its primary key. Returns null if not found.</summary>
    ValueTask<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);

    /// <summary>
    /// Persist a new entity. Callers must also write the corresponding
    /// <see cref="SyncOutboxEntry"/> in the same unit-of-work if sync is enabled.
    /// </summary>
    ValueTask AddAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Persist a batch atomically. Preferred over looped AddAsync.</summary>
    ValueTask AddRangeAsync(IReadOnlyList<TEntity> entities, CancellationToken ct = default);

    /// <summary>Update a mutable entity. Records are immutable by design; use sparingly.</summary>
    ValueTask UpdateAsync(TEntity entity, CancellationToken ct = default);

    /// <summary>Hard-delete. For GDPR "Nuclear Option" wipes only.</summary>
    ValueTask DeleteAsync(TId id, CancellationToken ct = default);
}
