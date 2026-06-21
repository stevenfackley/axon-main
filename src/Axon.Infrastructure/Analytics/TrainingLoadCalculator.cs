using System.Collections.Generic;

namespace Axon.Infrastructure.Analytics;

/// <summary>
/// Immutable snapshot of a single day's training-load metrics.
/// </summary>
/// <param name="Date">Calendar date this data point represents.</param>
/// <param name="Ctl">
///     Chronic Training Load — exponentially-weighted moving average with a
///     42-day time constant. Represents long-term fitness base.
/// </param>
/// <param name="Atl">
///     Acute Training Load — EWMA with a 7-day time constant. Tracks
///     short-term fatigue.
/// </param>
/// <param name="Tsb">
///     Training Stress Balance ("form") = previous day's CTL − previous
///     day's ATL. Positive when fresh/tapering; negative when fatigued.
/// </param>
public sealed record TrainingLoadPoint(DateOnly Date, double Ctl, double Atl, double Tsb);

/// <summary>
/// Pure, stateless calculator for the TrainingPeaks CTL / ATL / TSB trio.
///
/// <para>
/// Algorithm — standard EWMA update rule:<br/>
/// <c>today = yesterday + (todayLoad − yesterday) × α</c><br/>
/// where <c>α = 1 − exp(−1 / τ)</c> and <c>τ</c> is the time constant in days.
/// </para>
///
/// <para>
/// Seeding convention: on the very first day, CTL = ATL = that day's load,
/// and TSB = 0 (because there is no "previous day" to compute balance from).
/// This avoids a cold-start period of artificially low values.
/// </para>
///
/// <para>
/// Input must be ordered chronologically (oldest first). The method does not
/// sort — callers are responsible for ordering. Gaps (missing days) are not
/// interpolated; each entry is treated as the next consecutive EWMA step.
/// </para>
/// </summary>
public static class TrainingLoadCalculator
{
    // Time constants in days (TrainingPeaks convention).
    private const double CtlTau = 42.0;
    private const double AtlTau = 7.0;

    // Pre-computed decay multipliers: α = 1 − exp(−1/τ).
    private static readonly double _alphaCtl = 1.0 - Math.Exp(-1.0 / CtlTau);
    private static readonly double _alphaAtl = 1.0 - Math.Exp(-1.0 / AtlTau);

    /// <summary>
    /// Computes CTL, ATL, and TSB for each day in the supplied load series.
    /// </summary>
    /// <param name="dailyLoads">
    ///     Chronologically ordered sequence of (date, load) pairs. The load
    ///     value should be a dimensionless daily training-stress score — for
    ///     Axon data this is derived from <see cref="Axon.Core.Domain.BiometricType.StrainScore"/>.
    /// </param>
    /// <returns>
    ///     A <see cref="List{T}"/> of <see cref="TrainingLoadPoint"/> values,
    ///     one per input entry, in the same order.
    /// </returns>
    public static List<TrainingLoadPoint> Calculate(
        IReadOnlyList<(DateOnly Date, double Load)> dailyLoads)
    {
        var result = new List<TrainingLoadPoint>(dailyLoads.Count);

        if (dailyLoads.Count == 0)
            return result;

        // Seed: first day's CTL and ATL equal the first day's load.
        double ctl = dailyLoads[0].Load;
        double atl = dailyLoads[0].Load;
        result.Add(new TrainingLoadPoint(dailyLoads[0].Date, ctl, atl, Tsb: 0.0));

        for (int i = 1; i < dailyLoads.Count; i++)
        {
            // TSB uses *previous* day's values (before today's load is applied).
            double tsb = ctl - atl;

            double load = dailyLoads[i].Load;
            ctl = ctl + (load - ctl) * _alphaCtl;
            atl = atl + (load - atl) * _alphaAtl;

            result.Add(new TrainingLoadPoint(dailyLoads[i].Date, ctl, atl, tsb));
        }

        return result;
    }
}
