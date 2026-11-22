using System.Runtime.CompilerServices;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;

namespace Axon.Tests;

/// <summary>
/// Covers the producer/consumer hand-off in <see cref="IngestionOrchestrator.IngestAsync"/>
/// when persisting a batch fails.
/// </summary>
public sealed class IngestionOrchestratorTests
{
    [Fact]
    public async Task Ingest_WhenPersistFails_StopsTheDriverAndRethrows()
    {
        var driver = new EndlessDriver();
        var orchestrator = new IngestionOrchestrator(
            new FailingRepository(), new UnusedInference(), NullLogger<IngestionOrchestrator>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await orchestrator.IngestAsync(driver, DateTimeOffset.UtcNow.AddDays(-1)));

        // The driver must have been cancelled and disposed, not left blocked on the full channel.
        Assert.True(driver.Ended.Task.IsCompleted);
    }

    /// <summary>Yields events until cancelled; signals when its enumerator is disposed.</summary>
    private sealed class EndlessDriver : IBiometricDriver
    {
        public TaskCompletionSource Ended { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DriverId => "endless";
        public string DisplayName => "Endless";
        public bool SupportsOffline => true;

        public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default) => ValueTask.FromResult(true);
        public ValueTask AuthoriseAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
            DateTimeOffset since, [EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                var ts = since;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    ts = ts.AddSeconds(1);
                    yield return new BiometricEvent(Guid.NewGuid(), ts, BiometricType.HeartRate, 60, "bpm",
                        new SourceMetadata("dev", "Test", null, 1f, ts));
                    await Task.Yield();
                }
            }
            finally
            {
                Ended.TrySetResult();
            }
        }

        public IAsyncEnumerable<BiometricEvent> StreamLiveAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingRepository : IBiometricRepository
    {
        public ValueTask IngestBatchAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default) =>
            throw new InvalidOperationException("persist failed");

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask AddAsync(BiometricEvent entity, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> entities, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask UpdateAsync(BiometricEvent entity, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, int bucketSizeSeconds,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedInference : IInferenceService
    {
        public ValueTask<IReadOnlyList<AnomalyResult>> DetectAnomaliesAsync(
            IReadOnlyList<BiometricEvent> heartRateSamples, IReadOnlyList<BiometricEvent> hrvSamples,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<ForecastPoint>> ForecastRecoveryAsync(
            IReadOnlyList<BiometricEvent> sleepHistory, IReadOnlyList<BiometricEvent> strainHistory,
            int horizonDays = 7, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
