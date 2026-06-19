using Axon.UI.Rendering;
using Axon.UI.ViewModels;

namespace Axon.Tests;

/// <summary>
/// Unit tests for the LTTB (Largest Triangle Three Buckets) downsampler.
///
/// Verifies: passthrough conditions, output length, first/last point invariants,
/// monotone timestamps, and threshold clamping.
/// </summary>
public class LttbDownsamplerTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ChartPoint[] MakePoints(int count, Func<int, double>? valueFunc = null)
    {
        var base_ = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count)
            .Select(i => new ChartPoint(
                Timestamp: base_.AddSeconds(i),
                Value: valueFunc?.Invoke(i) ?? i))
            .ToArray();
    }

    // ── Passthrough (no downsampling needed) ──────────────────────────────────

    [Fact]
    public void Downsample_EmptyInput_ReturnsEmpty()
    {
        var result = LttbDownsampler.Downsample(Array.Empty<ChartPoint>());
        Assert.Empty(result);
    }

    [Fact]
    public void Downsample_SinglePoint_ReturnsSinglePoint()
    {
        var pts = MakePoints(1);
        var result = LttbDownsampler.Downsample(pts, threshold: 2048);

        Assert.Single(result);
        Assert.Equal(pts[0], result[0]);
    }

    [Fact]
    public void Downsample_TwoPoints_ReturnsBothPoints()
    {
        var pts = MakePoints(2);
        var result = LttbDownsampler.Downsample(pts, threshold: 2048);

        Assert.Equal(2, result.Length);
        Assert.Equal(pts[0], result[0]);
        Assert.Equal(pts[1], result[1]);
    }

    [Fact]
    public void Downsample_CountBelowThreshold_ReturnsCopyOfAll()
    {
        var pts = MakePoints(100);
        var result = LttbDownsampler.Downsample(pts, threshold: 2048);

        Assert.Equal(100, result.Length);
        for (int i = 0; i < 100; i++)
            Assert.Equal(pts[i], result[i]);
    }

    [Fact]
    public void Downsample_CountEqualToThreshold_ReturnsCopyOfAll()
    {
        var pts = MakePoints(10);
        var result = LttbDownsampler.Downsample(pts, threshold: 10);

        Assert.Equal(10, result.Length);
    }

    // ── Downsampling behaviour ────────────────────────────────────────────────

    [Fact]
    public void Downsample_OutputLength_ExactlyThreshold()
    {
        var pts = MakePoints(5000);
        var result = LttbDownsampler.Downsample(pts, threshold: 200);

        Assert.Equal(200, result.Length);
    }

    [Fact]
    public void Downsample_FirstPoint_AlwaysFirst()
    {
        var pts = MakePoints(1000);
        var result = LttbDownsampler.Downsample(pts, threshold: 50);

        Assert.Equal(pts[0], result[0]);
    }

    [Fact]
    public void Downsample_LastPoint_AlwaysLast()
    {
        var pts = MakePoints(1000);
        var result = LttbDownsampler.Downsample(pts, threshold: 50);

        Assert.Equal(pts[^1], result[^1]);
    }

    [Fact]
    public void Downsample_OutputTimestamps_MonotonicallyIncreasing()
    {
        // LTTB selects within ordered buckets so timestamps must stay ordered.
        var pts = MakePoints(2000, i => Math.Sin(i * 0.1) * 50 + 70);
        var result = LttbDownsampler.Downsample(pts, threshold: 100);

        for (int i = 1; i < result.Length; i++)
            Assert.True(result[i].Timestamp > result[i - 1].Timestamp,
                $"Timestamp at [{i}] not greater than [{i-1}]");
    }

    // ── Threshold edge cases ──────────────────────────────────────────────────

    [Fact]
    public void Downsample_ThresholdBelowTwo_ClampedToTwo()
    {
        var pts = MakePoints(500);

        // threshold=0 or 1 must be clamped to 2
        var result0 = LttbDownsampler.Downsample(pts, threshold: 0);
        var result1 = LttbDownsampler.Downsample(pts, threshold: 1);

        Assert.Equal(2, result0.Length);
        Assert.Equal(2, result1.Length);
    }

    [Fact]
    public void Downsample_ThresholdTwo_ReturnsFirstAndLast()
    {
        var pts = MakePoints(100);
        var result = LttbDownsampler.Downsample(pts, threshold: 2);

        Assert.Equal(2, result.Length);
        Assert.Equal(pts[0], result[0]);
        Assert.Equal(pts[^1], result[1]);
    }

    [Fact]
    public void Downsample_SelectedPointsAllFromInput()
    {
        var pts = MakePoints(500);
        var ptSet = pts.ToHashSet();
        var result = LttbDownsampler.Downsample(pts, threshold: 50);

        // Every selected point must be a member of the original input.
        Assert.All(result, p => Assert.Contains(p, ptSet));
    }

    // ── Signal integrity ──────────────────────────────────────────────────────

    [Fact]
    public void Downsample_FlatSignal_AllOutputValuesEqual()
    {
        // Flat signal → LTTB can pick any point; all should equal 42.
        var pts = MakePoints(1000, _ => 42.0);
        var result = LttbDownsampler.Downsample(pts, threshold: 20);

        Assert.All(result, p => Assert.Equal(42.0, p.Value));
    }

    [Fact]
    public void Downsample_PeakSignal_PreservesPeak()
    {
        // Single peak at midpoint — LTTB should select it.
        var pts = MakePoints(1000, i => i == 500 ? 1000.0 : 0.0);
        var result = LttbDownsampler.Downsample(pts, threshold: 50);

        Assert.Contains(result, p => p.Value == 1000.0);
    }

    // ── Async wrapper ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DownsampleAsync_SmallInput_ReturnsCopyDirectly()
    {
        var pts = MakePoints(50);  // below 256 threshold for quick path
        var result = await LttbDownsampler.DownsampleAsync(pts, threshold: 2048);

        Assert.Equal(50, result.Length);
    }

    [Fact]
    public async Task DownsampleAsync_LargeInput_MatchesSyncResult()
    {
        var pts = MakePoints(3000, i => Math.Sin(i * 0.05) * 100);
        var syncResult = LttbDownsampler.Downsample(pts, threshold: 150);
        var asyncResult = await LttbDownsampler.DownsampleAsync(pts, threshold: 150);

        Assert.Equal(syncResult.Length, asyncResult.Length);
        for (int i = 0; i < syncResult.Length; i++)
            Assert.Equal(syncResult[i], asyncResult[i]);
    }

    [Fact]
    public async Task DownsampleAsync_CancelledToken_ThrowsOperationCancelled()
    {
        var pts = MakePoints(3000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => LttbDownsampler.DownsampleAsync(pts, threshold: 100, ct: cts.Token).AsTask());
    }
}
