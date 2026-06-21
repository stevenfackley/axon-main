using Axon.Core.Domain;
using Axon.Core.Ports;
using Axon.Infrastructure.Insights;

namespace Axon.Tests;

/// <summary>
/// Unit tests for <see cref="InsightExplanationService"/>.
///
/// Coverage strategy:
///   • Empty / below-threshold input → explicit "needs more data" path.
///   • Anomaly severity bands (high, moderate, mild) based on p-value.
///   • HRV-specific percentile phrasing.
///   • Forecast: next low-recovery day detection, band labels, no-low-day path.
///   • Confidence label thresholds (High ≥ 60, Moderate ≥ 21, Low &lt; 21).
///   • Determinism: same input always produces the same output.
/// </summary>
public class InsightExplanationServiceTests
{
    private readonly InsightExplanationService _sut = new();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AnomalyResult MakeAnomaly(bool isAnomaly, double pValue = 1.0) =>
        new(
            Timestamp: DateTimeOffset.UtcNow,
            BiometricType: BiometricType.HeartRate,
            IsAnomaly: isAnomaly,
            Score: isAnomaly ? 5.0 : 0.0,
            PValue: pValue);

    private static IReadOnlyList<AnomalyResult> MakeResults(int total, int anomalies, double pValue = 0.05) =>
        Enumerable.Range(0, total)
            .Select(i => MakeAnomaly(i < anomalies, i < anomalies ? pValue : 1.0))
            .ToList();

    private static ForecastPoint MakeForecast(int dayOffset, float readiness) =>
        new(
            Date: new DateTimeOffset(DateTime.UtcNow.Date.AddDays(dayOffset), TimeSpan.Zero),
            PredictedReadiness: readiness,
            LowerBound: readiness - 5f,
            UpperBound: readiness + 5f);

    // ── ExplainAnomalies — empty / insufficient data ───────────────────────────

    [Fact]
    public void ExplainAnomalies_EmptyList_ReturnsInsufficientData()
    {
        var result = _sut.ExplainAnomalies(Array.Empty<AnomalyResult>(), baselineDays: 30);

        Assert.Contains("Insufficient Data", result.Title);
        Assert.Equal(0, result.SampleSize);
        Assert.Contains("Low", result.Confidence);
    }

    [Fact]
    public void ExplainAnomalies_BelowMinSamples_ReturnsNeedsMoreDataCaveat()
    {
        var results = MakeResults(total: 5, anomalies: 0);

        var result = _sut.ExplainAnomalies(results, baselineDays: 7);

        Assert.Contains("Insufficient Data", result.Title);
        Assert.Contains("5", result.Detail);        // mentions the count
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public void ExplainAnomalies_ExactlyAtMinSamples_DoesNotReturnInsufficientData()
    {
        // 12 samples is the threshold (MinSpikeDetectorSamples)
        var results = MakeResults(total: 12, anomalies: 0);

        var result = _sut.ExplainAnomalies(results, baselineDays: 14);

        Assert.DoesNotContain("Insufficient Data", result.Title);
        Assert.Equal(12, result.SampleSize);
    }

    // ── ExplainAnomalies — no anomalies detected ──────────────────────────────

    [Fact]
    public void ExplainAnomalies_NoAnomaliesDetected_ReturnsAllClear()
    {
        var results = MakeResults(total: 90, anomalies: 0);

        var result = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Equal("All Clear", result.Title);
        Assert.Contains("No anomalies", result.Detail);
        Assert.Equal(90, result.SampleSize);
    }

    // ── ExplainAnomalies — severity bands ─────────────────────────────────────

    [Fact]
    public void ExplainAnomalies_HighSeverityPValue_ReturnsHighSeverityTitle()
    {
        // p-value < 0.01 → high-severity
        var results = MakeResults(total: 90, anomalies: 10, pValue: 0.005);

        var result = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Equal("High-Severity Anomaly Detected", result.Title);
        Assert.Contains("high-severity alert", result.Detail);
        Assert.Contains("statistically significant", result.Detail);
    }

    [Fact]
    public void ExplainAnomalies_ModeratePValue_ReturnsModerateSeverityTitle()
    {
        // p-value between 0.01 and 0.03 → moderate
        var results = MakeResults(total: 90, anomalies: 5, pValue: 0.02);

        var result = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Equal("Moderate Anomaly Detected", result.Title);
        Assert.Contains("moderate anomaly", result.Detail);
    }

    [Fact]
    public void ExplainAnomalies_MildPValue_ReturnsMildTitle()
    {
        // p-value >= 0.03 but still flagged → mild
        var results = MakeResults(total: 90, anomalies: 3, pValue: 0.04);

        var result = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Equal("Mild Deviation Noted", result.Title);
        Assert.Contains("mild deviation", result.Detail);
    }

    [Fact]
    public void ExplainAnomalies_Detail_ContainsBaselineDays()
    {
        var results = MakeResults(total: 90, anomalies: 5, pValue: 0.005);

        var result = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Contains("90-day baseline", result.Detail);
    }

    // ── ExplainHrvAnomalies ───────────────────────────────────────────────────

    [Fact]
    public void ExplainHrvAnomalies_EmptyList_ReturnsInsufficientData()
    {
        var result = _sut.ExplainHrvAnomalies(Array.Empty<AnomalyResult>(), baselineDays: 30);

        Assert.Contains("Insufficient Data", result.Title);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public void ExplainHrvAnomalies_BelowMinSamples_ReturnsNeedsMoreData()
    {
        var results = MakeResults(total: 8, anomalies: 2);

        var result = _sut.ExplainHrvAnomalies(results, baselineDays: 8);

        Assert.Contains("Insufficient Data", result.Title);
        Assert.Contains("Low", result.Confidence);
    }

    [Fact]
    public void ExplainHrvAnomalies_NoAnomalies_ReturnsWithinBaseline()
    {
        var results = MakeResults(total: 30, anomalies: 0);

        var result = _sut.ExplainHrvAnomalies(results, baselineDays: 30);

        Assert.Equal("HRV Within Baseline", result.Title);
    }

    [Fact]
    public void ExplainHrvAnomalies_Anomalies_ContainsBottomPercentilePhrasing()
    {
        // 25 anomalies out of 100 = bottom 25%
        var results = MakeResults(total: 100, anomalies: 25);

        var result = _sut.ExplainHrvAnomalies(results, baselineDays: 90);

        Assert.Equal("HRV Deviation Detected", result.Title);
        Assert.Contains("bottom", result.Detail);
        Assert.Contains("%", result.Detail);
        Assert.Contains("baseline", result.Detail);
    }

    [Fact]
    public void ExplainHrvAnomalies_HighPrevalence_UsesSignificantlyWording()
    {
        // 30 out of 100 = 30% → "significantly"
        var results = MakeResults(total: 100, anomalies: 30);

        var result = _sut.ExplainHrvAnomalies(results, baselineDays: 90);

        Assert.Contains("significantly", result.Detail);
    }

    [Fact]
    public void ExplainHrvAnomalies_LowPrevalence_UsesSlightlyWording()
    {
        // 5 out of 100 = 5% → "slightly"
        var results = MakeResults(total: 100, anomalies: 5);

        var result = _sut.ExplainHrvAnomalies(results, baselineDays: 90);

        Assert.Contains("slightly", result.Detail);
    }

    // ── ExplainForecast ───────────────────────────────────────────────────────

    [Fact]
    public void ExplainForecast_EmptyForecast_ReturnsInsufficientData()
    {
        var result = _sut.ExplainForecast(Array.Empty<ForecastPoint>(), trainingDays: 14);

        Assert.Contains("Insufficient Data", result.Title);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public void ExplainForecast_NoLowDaysInWindow_StatesNoneExpected()
    {
        var forecast = new[]
        {
            MakeForecast(1, 75f),
            MakeForecast(2, 80f),
            MakeForecast(3, 70f),
            MakeForecast(4, 65f),
            MakeForecast(5, 72f),
            MakeForecast(6, 68f),
            MakeForecast(7, 74f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.Equal("Recovery Outlook", result.Title);
        Assert.Contains("No low-recovery days", result.Detail);
        Assert.Contains("high", result.Detail);   // avg ~72 → high band
    }

    [Fact]
    public void ExplainForecast_HasLowDay_IdentifiesNextLowRecoveryDay()
    {
        var tomorrow = DateTime.UtcNow.Date.AddDays(1);
        var forecast = new[]
        {
            MakeForecast(1, 65f),
            MakeForecast(2, 35f),   // first low
            MakeForecast(3, 38f),
            MakeForecast(4, 60f),
            MakeForecast(5, 70f),
            MakeForecast(6, 72f),
            MakeForecast(7, 68f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.Equal("Recovery Outlook", result.Title);
        Assert.Contains("next likely low-recovery day", result.Detail);
        // The second day (index 1) should be named.
        string expectedDayName = tomorrow.AddDays(1).ToString("dddd");
        Assert.Contains(expectedDayName, result.Detail);
    }

    [Fact]
    public void ExplainForecast_LowAverageReadiness_ReportsLowBand()
    {
        var forecast = new[]
        {
            MakeForecast(1, 30f),
            MakeForecast(2, 35f),
            MakeForecast(3, 28f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.Contains("low", result.Detail);
    }

    [Fact]
    public void ExplainForecast_ModerateAverageReadiness_ReportsModerate()
    {
        var forecast = new[]
        {
            MakeForecast(1, 55f),
            MakeForecast(2, 58f),
            MakeForecast(3, 52f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.Contains("moderate", result.Detail);
    }

    [Fact]
    public void ExplainForecast_LowTrainingDays_IncludesConfidenceCaveat()
    {
        var forecast = new[]
        {
            MakeForecast(1, 65f),
            MakeForecast(2, 70f)
        };

        // Below LowSampleThreshold (12) → caveat appended
        var result = _sut.ExplainForecast(forecast, trainingDays: 8);

        Assert.Contains("confidence is limited", result.Detail);
    }

    [Fact]
    public void ExplainForecast_SufficientTrainingDays_NoCaveat()
    {
        var forecast = new[]
        {
            MakeForecast(1, 70f),
            MakeForecast(2, 75f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.DoesNotContain("confidence is limited", result.Detail);
    }

    [Fact]
    public void ExplainForecast_CustomLowThreshold_Respected()
    {
        // With threshold = 60, readiness=55 should be flagged as low.
        var forecast = new[]
        {
            MakeForecast(1, 70f),
            MakeForecast(2, 55f),
            MakeForecast(3, 65f)
        };

        var result = _sut.ExplainForecast(forecast, trainingDays: 60, lowReadinessThreshold: 60f);

        Assert.Contains("next likely low-recovery day", result.Detail);
    }

    // ── Confidence label thresholds ───────────────────────────────────────────

    [Theory]
    [InlineData(0,  "Low")]
    [InlineData(1,  "Low")]
    [InlineData(11, "Low")]
    [InlineData(20, "Low")]
    [InlineData(21, "Moderate")]
    [InlineData(59, "Moderate")]
    [InlineData(60, "High")]
    [InlineData(90, "High")]
    public void BuildConfidence_ReturnsExpectedLabel(int n, string expectedPrefix)
    {
        var label = InsightExplanationService.BuildConfidence(n);

        Assert.StartsWith(expectedPrefix, label);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(90)]
    public void BuildConfidence_HighLabel_ContainsSampleCount(int n)
    {
        var label = InsightExplanationService.BuildConfidence(n);

        Assert.Contains($"n={n}", label);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void ExplainAnomalies_SameInput_AlwaysProducesSameOutput()
    {
        var results = MakeResults(total: 90, anomalies: 10, pValue: 0.005);

        var first  = _sut.ExplainAnomalies(results, baselineDays: 90);
        var second = _sut.ExplainAnomalies(results, baselineDays: 90);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ExplainHrvAnomalies_SameInput_AlwaysProducesSameOutput()
    {
        var results = MakeResults(total: 50, anomalies: 15);

        var first  = _sut.ExplainHrvAnomalies(results, baselineDays: 50);
        var second = _sut.ExplainHrvAnomalies(results, baselineDays: 50);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ExplainForecast_SameInput_AlwaysProducesSameOutput()
    {
        var forecast = new[]
        {
            MakeForecast(1, 60f),
            MakeForecast(2, 35f),
            MakeForecast(3, 70f)
        };

        var first  = _sut.ExplainForecast(forecast, trainingDays: 60);
        var second = _sut.ExplainForecast(forecast, trainingDays: 60);

        Assert.Equal(first, second);
    }

    // ── InsightExplanation record equality (structural) ───────────────────────

    [Fact]
    public void InsightExplanation_RecordEquality_WorksCorrectly()
    {
        var a = new InsightExplanation("Title", "Detail", "High (n=60)", 60);
        var b = new InsightExplanation("Title", "Detail", "High (n=60)", 60);
        var c = new InsightExplanation("Title", "Detail", "High (n=61)", 61);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── SampleSize propagation ────────────────────────────────────────────────

    [Fact]
    public void ExplainAnomalies_SampleSize_ReflectsInputCount()
    {
        var results = MakeResults(total: 45, anomalies: 0);

        var result = _sut.ExplainAnomalies(results, baselineDays: 45);

        Assert.Equal(45, result.SampleSize);
    }

    [Fact]
    public void ExplainForecast_SampleSize_ReflectsTrainingDays()
    {
        var forecast = new[] { MakeForecast(1, 70f) };

        var result = _sut.ExplainForecast(forecast, trainingDays: 42);

        Assert.Equal(42, result.SampleSize);
    }
}
