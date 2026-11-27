namespace Axon.Infrastructure.Persistence;

/// <summary>
/// The work that reads or writes the vault while the app runs: a sync or an import, the
/// analysis after a sync, the dashboard and Analysis Lab loads, the outbox relay.
///
/// The data erase calls <see cref="QuiesceAsync"/> before it closes anything: from that
/// moment new work is refused (there is no gap between the check and the start, both happen
/// under one lock), running work is cancelled, and the erase waits a bounded time for it to
/// end. If something is still running then, the erase is refused and the app carries on.
/// </summary>
public sealed class VaultActivity
{
    private readonly Lock _lock = new();
    private readonly List<Operation> _running = [];
    private bool _quiescing;

    /// <summary>
    /// Starts tracked work, or returns null when the vault is being erased. Dispose the
    /// operation when the work ends, and use its <see cref="Operation.Token"/> for the work.
    /// </summary>
    public Operation? TryBegin(string name, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_quiescing) return null;
            var operation = new Operation(this, name, ct);
            _running.Add(operation);
            return operation;
        }
    }

    /// <summary>
    /// Refuses new work, cancels what is running and waits up to <paramref name="bound"/>
    /// for it to end. Returns the names of the work still running: empty means the vault is
    /// quiet and stays closed to new work. Otherwise new work is allowed again, and the
    /// caller must not erase.
    /// </summary>
    public async Task<IReadOnlyList<string>> QuiesceAsync(TimeSpan bound)
    {
        Operation[] running;
        lock (_lock)
        {
            _quiescing = true;
            running = [.. _running];
        }

        foreach (var operation in running)
            operation.Cancel();

        if (running.Length > 0)
        {
            await Task.WhenAny(
                Task.WhenAll(running.Select(o => o.Ended)),
                Task.Delay(bound)).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (_running.Count == 0)
                return [];

            _quiescing = false;
            return _running.Select(o => o.Name).Distinct().ToList();
        }
    }

    private void End(Operation operation)
    {
        lock (_lock)
            _running.Remove(operation);
    }

    /// <summary>One piece of tracked work. Dispose it when the work ends.</summary>
    public sealed class Operation : IDisposable
    {
        private readonly VaultActivity _owner;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        internal Operation(VaultActivity owner, string name, CancellationToken ct)
        {
            _owner = owner;
            Name = name;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        /// <summary>What the person would call this work ("a Whoop sync").</summary>
        public string Name { get; }

        /// <summary>Cancelled when the caller's token is, or when an erase stops the work.</summary>
        public CancellationToken Token => _cts.Token;

        internal Task Ended => _ended.Task;

        internal void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It ended while the erase was stopping it.
            }
            catch (AggregateException)
            {
                // A cancellation callback threw; the work is being torn down either way.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.End(this);
            _ended.TrySetResult();
            _cts.Dispose();
        }
    }
}
