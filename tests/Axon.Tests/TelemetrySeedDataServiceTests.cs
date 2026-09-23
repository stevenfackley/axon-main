using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.UI.Application;

namespace Axon.Tests;

public class TelemetrySeedDataServiceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    public void IsEnabled_IsOptInOnly(string? value, bool expected)
        => Assert.Equal(expected, TelemetrySeedDataService.IsEnabled(value));

    [Fact]
    public async Task SeedIfEmptyAsync_EmptyRepository_IngestsOneBatch()
    {
        var repo = new RecordingRepository();
        var service = new TelemetrySeedDataService(repo);

        var seeded = await service.SeedIfEmptyAsync();

        Assert.True(seeded);
        var batch = Assert.Single(repo.Batches);
        Assert.NotEmpty(batch);
        Assert.All(batch, e => Assert.Equal(TelemetrySeedDataService.SeedDeviceId, e.Source.DeviceId));
    }

    [Fact]
    public async Task SeedIfEmptyAsync_AnyExistingDataOfAnyType_SkipsWithoutWriting()
    {
        var existing = new BiometricEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, BiometricType.SpO2, 97, "%",
            new SourceMetadata("dev", "vendor", null, 1f, DateTimeOffset.UtcNow));
        var repo = new RecordingRepository
        {
            LatestVitals = new Dictionary<BiometricType, BiometricEvent> { [BiometricType.SpO2] = existing },
        };
        var service = new TelemetrySeedDataService(repo);

        var seeded = await service.SeedIfEmptyAsync();

        Assert.False(seeded);
        Assert.Empty(repo.Batches);
    }

    [Fact]
    public void GenerateSeedEvents_IdsAreUniqueAndStableForTheSameClock()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 13, 37, 0, TimeSpan.Zero));

        var first = new TelemetrySeedDataService(new RecordingRepository(), clock).GenerateSeedEvents();
        var second = new TelemetrySeedDataService(new RecordingRepository(), clock).GenerateSeedEvents();

        Assert.Equal(first.Count, first.Select(e => e.Id).Distinct().Count());
        Assert.Equal(first.Select(e => e.Id), second.Select(e => e.Id));
        Assert.Equal(first.Select(e => e.Value), second.Select(e => e.Value));
        Assert.All(first, e => Assert.True(e.Timestamp <= clock.GetUtcNow()));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository : IBiometricRepository
    {
        public IReadOnlyDictionary<BiometricType, BiometricEvent> LatestVitals { get; init; }
            = new Dictionary<BiometricType, BiometricEvent>();

        public List<IReadOnlyList<BiometricEvent>> Batches { get; } = [];

        public ValueTask<BiometricEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => ValueTask.FromResult<BiometricEvent?>(null);

        public ValueTask AddAsync(BiometricEvent entity, CancellationToken ct = default)
            => throw new NotSupportedException("Seeding must use IngestBatchAsync.");

        public ValueTask AddRangeAsync(IReadOnlyList<BiometricEvent> entities, CancellationToken ct = default)
            => throw new NotSupportedException("Seeding must use IngestBatchAsync.");

        public ValueTask UpdateAsync(BiometricEvent entity, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DeleteAsync(Guid id, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<BiometricEvent>> QueryRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<BiometricEvent>>([]);

        public async IAsyncEnumerable<BiometricEvent> StreamRangeAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<IReadOnlyList<AggregateBucket>> GetAggregatesAsync(
            BiometricType type, DateTimeOffset from, DateTimeOffset to,
            int bucketSizeSeconds, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<AggregateBucket>>([]);

        public ValueTask<IReadOnlyDictionary<BiometricType, BiometricEvent>> GetLatestVitalsAsync(
            CancellationToken ct = default)
            => ValueTask.FromResult(LatestVitals);

        public ValueTask IngestBatchAsync(IReadOnlyList<BiometricEvent> events, CancellationToken ct = default)
        {
            Batches.Add(events);
            return ValueTask.CompletedTask;
        }
    }
}
