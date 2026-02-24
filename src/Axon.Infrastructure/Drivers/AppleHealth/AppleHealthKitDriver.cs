// Apple HealthKit driver – only compiled when targeting net9.0-ios.
#if IOS
using System.Runtime.CompilerServices;
using Axon.Core.Domain;
using Axon.Core.Ports;
using Foundation;
using HealthKit;

namespace Axon.Infrastructure.Drivers.AppleHealth;

/// <summary>
/// <see cref="IBiometricDriver"/> implementation for Apple HealthKit.
///
/// Reads historical and live biometric data from the Apple Health store
/// on iOS and emits <see cref="BiometricEvent"/> records normalised to the
/// Axon Common Schema (ACS) via <see cref="HealthKitNormalizationMapper"/>.
///
/// Lifecycle
/// ──────────
/// 1. Call <see cref="AuthoriseAsync"/> once during onboarding to request
///    HealthKit read permissions. The system displays the permission sheet.
/// 2. Call <see cref="FetchSinceAsync"/> to pull historical events.
/// 3. Call <see cref="StreamLiveAsync"/> to subscribe to new samples via
///    <c>HKObserverQuery</c> background delivery.
///
/// Threading
/// ──────────
/// HealthKit callback threads are background threads. All events are yielded
/// via <c>IAsyncEnumerable</c> so the consumer controls scheduling.
///
/// Air-Gap
/// ───────
/// HealthKit reads are fully local — no outbound network calls are made.
/// <see cref="SupportsOffline"/> is <c>true</c>.
/// </summary>
public sealed class AppleHealthKitDriver : IBiometricDriver
{
    // ── IBiometricDriver identity ──────────────────────────────────────────────

    public string DriverId    => "apple.healthkit";
    public string DisplayName => "Apple Health";
    public bool   SupportsOffline => true;

    // ── HealthKit state ────────────────────────────────────────────────────────

    private readonly HKHealthStore _store = new();
    private bool _authorised;

    // All HKQuantityType identifiers Axon reads. Must mirror Entitlements.plist.
    private static readonly string[] QuantityTypeIds =
    [
        "HKQuantityTypeIdentifierHeartRate",
        "HKQuantityTypeIdentifierHeartRateVariabilitySDNN",
        "HKQuantityTypeIdentifierRestingHeartRate",
        "HKQuantityTypeIdentifierRespiratoryRate",
        "HKQuantityTypeIdentifierOxygenSaturation",
        "HKQuantityTypeIdentifierStepCount",
        "HKQuantityTypeIdentifierActiveEnergyBurned",
        "HKQuantityTypeIdentifierBasalEnergyBurned",
        "HKQuantityTypeIdentifierVO2Max",
        "HKQuantityTypeIdentifierCyclingPower",
        "HKQuantityTypeIdentifierBodyMass",
        "HKQuantityTypeIdentifierBodyFatPercentage",
        "HKQuantityTypeIdentifierLeanBodyMass",
        "HKQuantityTypeIdentifierBodyTemperature",
        "HKQuantityTypeIdentifierBloodGlucose",
        "HKQuantityTypeIdentifierBloodPressureSystolic",
        "HKQuantityTypeIdentifierBloodPressureDiastolic",
    ];

    // ── IBiometricDriver: availability & auth ──────────────────────────────────

    /// <inheritdoc/>
    public ValueTask<bool> IsAvailableAsync(CancellationToken ct = default)
        => ValueTask.FromResult(HKHealthStore.IsHealthDataAvailable);

    /// <inheritdoc/>
    /// <remarks>
    /// Idempotent — safe to call multiple times.
    /// Must be called from the main UI thread (UIKit requirement).
    /// Throws <see cref="InvalidOperationException"/> if HealthKit is unavailable
    /// (e.g., iPad without HealthKit support, or iOS Simulator).
    /// </remarks>
    public async ValueTask AuthoriseAsync(CancellationToken ct = default)
    {
        if (!HKHealthStore.IsHealthDataAvailable)
            throw new InvalidOperationException(
                "HealthKit is not available on this device.");

        if (_authorised) return;

        var readTypes  = BuildReadTypes();
        var writeTypes = BuildWriteTypes();

        var tcs = new TaskCompletionSource<(bool Success, NSError? Error)>();
        _store.RequestAuthorization(writeTypes, readTypes, (success, error) =>
            tcs.SetResult((success, error)));

        var (success, error) = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

        if (!success || error is not null)
            throw new UnauthorizedAccessException(
                $"HealthKit authorization failed: {error?.LocalizedDescription ?? "user denied"}");

        _authorised = true;
    }

    // ── IBiometricDriver: historical fetch ─────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Executes one <c>HKSampleQuery</c> per quantity type and one for sleep,
    /// streaming results through the channel as they arrive.
    /// Large datasets (years of HR data) are paginated internally by HealthKit.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> FetchSinceAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureAuthorised();

        var correlationId = Guid.NewGuid().ToString("N");
        var predicate     = HKQuery.GetPredicateForSamples(
            startDate: (NSDate)since.UtcDateTime,
            endDate:   (NSDate)DateTime.UtcNow,
            options:   HKQueryOptions.StrictStartDate);

        // ── Quantity types ───────────────────────────────────────────────────
        foreach (var typeId in QuantityTypeIds)
        {
            ct.ThrowIfCancellationRequested();

            var hkType = HKQuantityType.Create(
                HKQuantityTypeIdentifier.FromString(typeId));

            if (hkType is null) continue;

            var (unit, hkUnit) = GetUnitForTypeId(typeId);
            if (unit is null || hkUnit is null) continue;

            await foreach (var evt in QueryQuantitySamplesAsync(
                               hkType, hkUnit, unit, predicate, correlationId, ct))
            {
                yield return evt;
            }
        }

        // ── Sleep ────────────────────────────────────────────────────────────
        ct.ThrowIfCancellationRequested();
        await foreach (var sleepEvt in QuerySleepAsync(predicate, correlationId, ct))
        {
            yield return sleepEvt;
        }
    }

    // ── IBiometricDriver: live streaming ───────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// Subscribes to <c>HKObserverQuery</c> for all read types.
    /// Background delivery is enabled in Entitlements.plist so the stream
    /// receives new samples even when Axon is not in the foreground.
    /// Call <see cref="FetchSinceAsync"/> after each notification to pull the
    /// delta — HealthKit does not deliver the sample payload in the observer callback.
    /// </remarks>
    public async IAsyncEnumerable<BiometricEvent> StreamLiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureAuthorised();

        // Channel used to bridge HealthKit observer callbacks → IAsyncEnumerable
        var channel = System.Threading.Channels.Channel.CreateUnbounded<BiometricEvent>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });

        var queries = new List<HKObserverQuery>();
        var lastFetch = DateTimeOffset.UtcNow;

        // Register an observer for each quantity type
        foreach (var typeId in QuantityTypeIds)
        {
            ct.ThrowIfCancellationRequested();
            var hkType = HKQuantityType.Create(HKQuantityTypeIdentifier.FromString(typeId));
            if (hkType is null) continue;

            var query = new HKObserverQuery(hkType, predicate: null, updateHandler: async (q, completionHandler, error) =>
            {
                if (error is not null || ct.IsCancellationRequested)
                {
                    completionHandler();
                    return;
                }

                // Fetch delta since last successful pull
                await foreach (var evt in FetchSinceAsync(lastFetch, ct))
                {
                    await channel.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
                }

                lastFetch = DateTimeOffset.UtcNow;
                completionHandler(); // Signal HealthKit that processing is complete
            });

            _store.ExecuteQuery(query);
            queries.Add(query);
        }

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }
        finally
        {
            // Stop all observer queries on cancellation
            foreach (var q in queries)
                _store.StopQuery(q);

            channel.Writer.Complete();
        }
    }

    // ── Private Helpers ────────────────────────────────────────────────────────

    private void EnsureAuthorised()
    {
        if (!_authorised)
            throw new InvalidOperationException(
                "Call AuthoriseAsync() before fetching HealthKit data.");
    }

    private async IAsyncEnumerable<BiometricEvent> QueryQuantitySamplesAsync(
        HKQuantityType hkType,
        HKUnit hkUnit,
        string acsUnit,
        NSPredicate predicate,
        string correlationId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<List<HKQuantitySample>>();

        var query = new HKSampleQuery(
            sampleType:  hkType,
            predicate:   predicate,
            limit:       HKSampleQuery.NoLimit,
            sortDescriptors: [new NSSortDescriptor(HKSample.SortIdentifierStartDate, ascending: true)],
            resultsHandler: (_, results, error) =>
            {
                if (error is not null)
                {
                    tcs.SetException(new InvalidOperationException(
                        $"HealthKit query failed for {hkType}: {error.LocalizedDescription}"));
                    return;
                }

                tcs.SetResult(results?.OfType<HKQuantitySample>().ToList() ?? []);
            });

        _store.ExecuteQuery(query);
        var samples = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

        foreach (var s in samples)
        {
            ct.ThrowIfCancellationRequested();

            var deviceId = s.Device?.UdiDeviceIdentifier ?? s.Device?.Name ?? "Apple";
            var firmware = s.Device?.FirmwareVersion;

            var result = new HKQuantitySampleResult(
                TypeIdentifier:  hkType.Identifier,
                StartDate:       (DateTimeOffset)(DateTime)s.StartDate,
                EndDate:         (DateTimeOffset)(DateTime)s.EndDate,
                Value:           s.Quantity.GetDoubleValue(hkUnit),
                HkUnit:          hkUnit.UnitString,
                DeviceId:        deviceId,
                FirmwareVersion: firmware);

            var evt = HealthKitNormalizationMapper.MapQuantitySample(result, correlationId);
            if (evt is not null)
                yield return evt;
        }
    }

    private async IAsyncEnumerable<BiometricEvent> QuerySleepAsync(
        NSPredicate predicate,
        string correlationId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sleepType = HKCategoryType.Create(HKCategoryTypeIdentifier.SleepAnalysis)!;
        var tcs = new TaskCompletionSource<List<HKCategorySample>>();

        var query = new HKSampleQuery(
            sampleType:  sleepType,
            predicate:   predicate,
            limit:       HKSampleQuery.NoLimit,
            sortDescriptors: [new NSSortDescriptor(HKSample.SortIdentifierStartDate, ascending: true)],
            resultsHandler: (_, results, error) =>
            {
                if (error is not null)
                {
                    tcs.SetException(new InvalidOperationException(
                        $"HealthKit sleep query failed: {error.LocalizedDescription}"));
                    return;
                }

                tcs.SetResult(results?.OfType<HKCategorySample>().ToList() ?? []);
            });

        _store.ExecuteQuery(query);
        var samples = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

        foreach (var s in samples)
        {
            ct.ThrowIfCancellationRequested();

            var stage    = (HKSleepStage)(int)s.Value;
            var deviceId = s.Device?.UdiDeviceIdentifier ?? s.Device?.Name ?? "Apple";

            var result = new HKSleepSampleResult(
                Stage:     stage,
                StartDate: (DateTimeOffset)(DateTime)s.StartDate,
                EndDate:   (DateTimeOffset)(DateTime)s.EndDate,
                DeviceId:  deviceId);

            var evt = HealthKitNormalizationMapper.MapSleepSample(result, correlationId);
            if (evt is not null)
                yield return evt;
        }
    }

    // ── HK Type ↔ Unit mapping ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the <c>HKUnit</c> and ACS unit string for a given HealthKit type ID.
    /// Blood pressure is handled via the correlation query — returns null here.
    /// </summary>
    private static (HKUnit? hkUnit, string? acsUnit) GetUnitForTypeId(string typeId)
        => typeId switch
        {
            "HKQuantityTypeIdentifierHeartRate"              => (HKUnit.Count.UnitDividedBy(HKUnit.Minute), "bpm"),
            "HKQuantityTypeIdentifierHeartRateVariabilitySDNN" => (HKUnit.FromString("ms"), "ms"),
            "HKQuantityTypeIdentifierRestingHeartRate"       => (HKUnit.Count.UnitDividedBy(HKUnit.Minute), "bpm"),
            "HKQuantityTypeIdentifierRespiratoryRate"        => (HKUnit.Count.UnitDividedBy(HKUnit.Minute), "breaths/min"),
            "HKQuantityTypeIdentifierOxygenSaturation"       => (HKUnit.Percent, "%"),
            "HKQuantityTypeIdentifierStepCount"              => (HKUnit.Count, "steps"),
            "HKQuantityTypeIdentifierActiveEnergyBurned"     => (HKUnit.Kilocalorie, "kcal"),
            "HKQuantityTypeIdentifierBasalEnergyBurned"      => (HKUnit.Kilocalorie, "kcal"),
            "HKQuantityTypeIdentifierVO2Max"                 => (HKUnit.FromString("ml/kg/min"), "mL/kg/min"),
            "HKQuantityTypeIdentifierCyclingPower"           => (HKUnit.Watt, "W"),
            "HKQuantityTypeIdentifierBodyMass"               => (HKUnit.Gram.UnitMultipliedBy(1000), "kg"),
            "HKQuantityTypeIdentifierBodyFatPercentage"      => (HKUnit.Percent, "%"),
            "HKQuantityTypeIdentifierLeanBodyMass"           => (HKUnit.Gram.UnitMultipliedBy(1000), "kg"),
            "HKQuantityTypeIdentifierBodyTemperature"        => (HKUnit.DegreeCelsius, "°C"),
            "HKQuantityTypeIdentifierBloodGlucose"           => (HKUnit.FromString("mmol/L"), "mmol/L"),
            // BP individual samples (rare; usually come via correlation)
            "HKQuantityTypeIdentifierBloodPressureSystolic"  => (HKUnit.MillimeterOfMercury, "mmHg"),
            "HKQuantityTypeIdentifierBloodPressureDiastolic" => (HKUnit.MillimeterOfMercury, "mmHg"),
            _                                                => (null, null)
        };

    // ── HKSampleType set builders ──────────────────────────────────────────────

    private static NSSet<HKSampleType> BuildReadTypes()
    {
        var types = new List<HKSampleType>();

        foreach (var id in QuantityTypeIds)
        {
            var t = HKQuantityType.Create(HKQuantityTypeIdentifier.FromString(id));
            if (t is not null) types.Add(t);
        }

        var sleepType = HKCategoryType.Create(HKCategoryTypeIdentifier.SleepAnalysis);
        if (sleepType is not null) types.Add(sleepType);

        return NSSet<HKSampleType>.MakeNSObjectSet([.. types]);
    }

    private static NSSet<HKSampleType> BuildWriteTypes()
    {
        // Axon only writes back synthesised readiness data.
        var mindful = HKCategoryType.Create(HKCategoryTypeIdentifier.MindfulSession);
        return mindful is not null
            ? NSSet<HKSampleType>.MakeNSObjectSet([mindful])
            : NSSet<HKSampleType>.MakeNSObjectSet([]);
    }
}
#endif
