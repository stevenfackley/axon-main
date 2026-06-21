using Axon.Infrastructure.Analytics;

namespace Axon.Tests;

/// <summary>
/// Unit tests for <see cref="TrainingLoadCalculator"/>.
///
/// Verifies: empty input, single-day seed, EWMA convergence, ATL reacts
/// faster than CTL after a spike, TSB sign semantics, and spot-checked
/// hand-computed EWMA values.
/// </summary>
public sealed class TrainingLoadCalculatorTests
{
    // ── Constants (match the calculator) ─────────────────────────────────────

    // Pre-compute the two decay factors used in the implementation.
    private static readonly double AlphaCtl = 1.0 - Math.Exp(-1.0 / 42.0);
    private static readonly double AlphaAtl = 1.0 - Math.Exp(-1.0 / 7.0);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<(DateOnly Date, double Load)> MakeConstantLoad(
        int days, double load, DateOnly? start = null)
    {
        var origin = start ?? new DateOnly(2026, 1, 1);
        return Enumerable.Range(0, days)
            .Select(i => (origin.AddDays(i), load))
            .ToList();
    }

    private static List<(DateOnly Date, double Load)> MakeSingleDay(double load)
        => [(new DateOnly(2026, 1, 1), load)];

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public void Calculate_EmptyInput_ReturnsEmpty()
    {
        var result = TrainingLoadCalculator.Calculate([]);
        Assert.Empty(result);
    }

    // ── Single-day seed ───────────────────────────────────────────────────────

    [Fact]
    public void Calculate_SingleDay_SeedsCtlAndAtlToLoad()
    {
        // Seeding convention: first day CTL = ATL = load, TSB = 0.
        const double load = 80.0;
        var result = TrainingLoadCalculator.Calculate(MakeSingleDay(load));

        Assert.Single(result);
        Assert.Equal(load, result[0].Ctl, precision: 10);
        Assert.Equal(load, result[0].Atl, precision: 10);
        Assert.Equal(0.0, result[0].Tsb, precision: 10);
    }

    [Fact]
    public void Calculate_SingleDay_DatePreserved()
    {
        var date = new DateOnly(2026, 3, 15);
        var result = TrainingLoadCalculator.Calculate([(date, 50.0)]);

        Assert.Equal(date, result[0].Date);
    }

    // ── EWMA convergence ──────────────────────────────────────────────────────

    [Fact]
    public void Calculate_ConstantLoad_CtlConvergesTowardLoad()
    {
        // After 200 days at load=100, CTL should be very close to 100.
        const double load = 100.0;
        var result = TrainingLoadCalculator.Calculate(MakeConstantLoad(200, load));

        // CTL decay constant is 42 — after 200 days (≈ 5 tau) within 1% is expected.
        Assert.Equal(load, result[^1].Ctl, 0);   // integer precision sufficient
        Assert.InRange(result[^1].Ctl, 99.0, 100.0);
    }

    [Fact]
    public void Calculate_ConstantLoad_AtlConvergesTowardLoad()
    {
        const double load = 100.0;
        var result = TrainingLoadCalculator.Calculate(MakeConstantLoad(60, load));

        // ATL tau = 7 — after 60 days (≈ 8.5 tau) should be within 0.1%.
        Assert.InRange(result[^1].Atl, 99.9, 100.0);
    }

    // ── ATL reacts faster than CTL ────────────────────────────────────────────

    [Fact]
    public void Calculate_Spike_AtlRisesAboveCtlFaster()
    {
        // Build a baseline (50 days at 60) then a sharp spike day.
        var inputs = MakeConstantLoad(50, 60.0);
        var spikeDate = inputs[^1].Date.AddDays(1);
        inputs.Add((spikeDate, 250.0));

        var result = TrainingLoadCalculator.Calculate(inputs);
        var spikePoint = result[^1];

        // On the spike day, ATL should have risen more than CTL above baseline.
        var baseline = result[^2];
        double atlRise = spikePoint.Atl - baseline.Atl;
        double ctlRise = spikePoint.Ctl - baseline.Ctl;

        Assert.True(atlRise > ctlRise,
            $"ATL rise ({atlRise:F4}) should exceed CTL rise ({ctlRise:F4}) on spike day");
    }

    // ── TSB sign semantics ────────────────────────────────────────────────────

    [Fact]
    public void Calculate_Fatigued_TsbNegative()
    {
        // Establish a low-load baseline, then abruptly switch to very high load.
        // ATL (τ=7) rises faster than CTL (τ=42), so ATL > CTL → TSB < 0.
        var inputs = MakeConstantLoad(30, 40.0);    // low baseline
        var hiStart = inputs[^1].Date.AddDays(1);
        inputs.AddRange(MakeConstantLoad(14, 250.0, hiStart)); // sustained heavy load

        var result = TrainingLoadCalculator.Calculate(inputs);
        var lastTsb = result[^1].Tsb;
        Assert.True(lastTsb < 0,
            $"Expected negative TSB when fatigued, got {lastTsb:F4}");
    }

    [Fact]
    public void Calculate_Tapering_TsbBecomesPositive()
    {
        // Build up fatigue, then rest.
        var inputs = MakeConstantLoad(21, 150.0);             // build phase
        var restStart = inputs[^1].Date.AddDays(1);
        inputs.AddRange(MakeConstantLoad(14, 0.0, restStart)); // taper/rest

        var result = TrainingLoadCalculator.Calculate(inputs);
        var lastTsb = result[^1].Tsb;

        Assert.True(lastTsb > 0,
            $"Expected positive TSB after taper, got {lastTsb:F4}");
    }

    // ── Hand-computed EWMA spot checks ────────────────────────────────────────

    [Fact]
    public void Calculate_TwoDays_HandComputedEwmaValues()
    {
        // Day 1 seed: CTL1 = ATL1 = L1 = 100, TSB1 = 0.
        // Day 2: CTL2 = CTL1 + (L2 - CTL1) * alphaCtl
        //        ATL2 = ATL1 + (L2 - ATL1) * alphaAtl
        //        TSB2 = CTL1 - ATL1  (previous day) = 100 - 100 = 0.
        const double l1 = 100.0;
        const double l2 = 160.0;

        double expectedCtl2 = l1 + (l2 - l1) * AlphaCtl;
        double expectedAtl2 = l1 + (l2 - l1) * AlphaAtl;
        double expectedTsb2 = l1 - l1; // prev CTL − prev ATL = 0

        var result = TrainingLoadCalculator.Calculate([
            (new DateOnly(2026, 1, 1), l1),
            (new DateOnly(2026, 1, 2), l2),
        ]);

        Assert.Equal(2, result.Count);
        Assert.Equal(expectedCtl2, result[1].Ctl, precision: 10);
        Assert.Equal(expectedAtl2, result[1].Atl, precision: 10);
        Assert.Equal(expectedTsb2, result[1].Tsb, precision: 10);
    }

    [Fact]
    public void Calculate_ThreeDays_HandComputedEwmaValues()
    {
        // Extend the two-day check by one more day.
        const double l1 = 100.0, l2 = 160.0, l3 = 80.0;

        double ctl2 = l1 + (l2 - l1) * AlphaCtl;
        double atl2 = l1 + (l2 - l1) * AlphaAtl;

        double expectedCtl3 = ctl2 + (l3 - ctl2) * AlphaCtl;
        double expectedAtl3 = atl2 + (l3 - atl2) * AlphaAtl;
        double expectedTsb3 = ctl2 - atl2; // previous day's CTL − ATL

        var result = TrainingLoadCalculator.Calculate([
            (new DateOnly(2026, 1, 1), l1),
            (new DateOnly(2026, 1, 2), l2),
            (new DateOnly(2026, 1, 3), l3),
        ]);

        Assert.Equal(3, result.Count);
        Assert.Equal(expectedCtl3, result[2].Ctl, precision: 10);
        Assert.Equal(expectedAtl3, result[2].Atl, precision: 10);
        Assert.Equal(expectedTsb3, result[2].Tsb, precision: 10);
    }

    // ── Output ordering ───────────────────────────────────────────────────────

    [Fact]
    public void Calculate_OutputDates_MatchInputOrder()
    {
        var inputs = MakeConstantLoad(10, 75.0);
        var result = TrainingLoadCalculator.Calculate(inputs);

        Assert.Equal(inputs.Count, result.Count);
        for (int i = 0; i < inputs.Count; i++)
            Assert.Equal(inputs[i].Date, result[i].Date);
    }
}
