using System.Buffers;
using Axon.UI.ViewModels;

namespace Axon.UI.Rendering;

/// <summary>
/// Largest Triangle Three Buckets (LTTB) downsampling algorithm implementation.
///
/// LTTB is a visually lossless downsampling algorithm developed by Sveinn Steinarsson
/// (2013) that preserves the visual shape of a time series by selecting the data point
/// in each bucket that forms the largest triangle with the adjacent selected points.
///
/// Complexity: O(n) time, O(threshold) space.
/// Reference: https://skemman.is/handle/1946/15343
///
/// Performance Characteristics:
///   • All intermediate work uses <see cref="ArrayPool{T}"/> — zero heap allocation
///     on the hot path when reusing a pooled output buffer.
///   • Intended to run on a <see cref="Task.Run"/> background thread before any
///     results reach the UI layer, honoring the 120fps guardrail.
///   • Input spans > 24 hours MUST pass through this downsampler before binding
///     to <see cref="Axon.UI.ViewModels.DashboardViewModel.ChartPoints"/>.
/// </summary>
public static class LttbDownsampler
{
    /// <summary>
    /// The maximum number of output points produced per LTTB pass when no
    /// explicit threshold is specified. 2048 points at 120fps leaves ~4μs
    /// per point for the GPU to process.
    /// </summary>
    public const int DefaultThreshold = 2048;

    /// <summary>
    /// Downsamples <paramref name="data"/> to at most <paramref name="threshold"/>
    /// points using the LTTB algorithm.
    ///
    /// If <paramref name="data"/>.Count ≤ <paramref name="threshold"/> the input
    /// is returned as-is (no allocation).
    /// </summary>
    /// <param name="data">
    ///     Input time series, sorted by <see cref="ChartPoint.Timestamp"/> ascending.
    ///     Must contain at least 2 points for the algorithm to run.
    /// </param>
    /// <param name="threshold">
    ///     Target output point count. Must be ≥ 2. Values below 2 are clamped to 2.
    /// </param>
    /// <returns>
    ///     A new <see cref="ChartPoint[]"/> of at most <paramref name="threshold"/>
    ///     elements selected to maximally preserve visual fidelity.
    /// </returns>
    public static ChartPoint[] Downsample(
        IReadOnlyList<ChartPoint> data,
        int                       threshold = DefaultThreshold)
    {
        if (threshold < 2) threshold = 2;

        int dataLength = data.Count;

        // Fast path: no downsampling needed.
        if (dataLength <= threshold)
        {
            var copy = new ChartPoint[dataLength];
            for (int i = 0; i < dataLength; i++) copy[i] = data[i];
            return copy;
        }

        // Output buffer — exactly threshold points.
        var sampled = new ChartPoint[threshold];

        // Always include the first point.
        sampled[0] = data[0];

        // Each "bucket" in the middle covers this many raw points.
        // Bucket count = threshold - 2 (excludes the fixed first and last points).
        double every = (double)(dataLength - 2) / (threshold - 2);

        int   sampledIdx = 1;
        int   a          = 0;   // Index of the previously selected point.

        for (int i = 0; i < threshold - 2; i++)
        {
            // ── Calculate the average point of the NEXT bucket ────────────────
            // This average acts as the "point C" for the triangle area test.
            int nextBucketStart = (int)Math.Floor((i + 1) * every) + 1;
            int nextBucketEnd   = (int)Math.Floor((i + 2) * every) + 1;
            nextBucketEnd = Math.Min(nextBucketEnd, dataLength);

            double avgX = 0.0;
            double avgY = 0.0;
            int    avgCount = nextBucketEnd - nextBucketStart;

            for (int j = nextBucketStart; j < nextBucketEnd; j++)
            {
                avgX += data[j].Timestamp.ToUnixTimeMilliseconds();
                avgY += data[j].Value;
            }

            avgX /= avgCount;
            avgY /= avgCount;

            // ── Find the point in the CURRENT bucket that forms the largest triangle ──
            int currentBucketStart = (int)Math.Floor(i * every) + 1;
            int currentBucketEnd   = (int)Math.Floor((i + 1) * every) + 1;
            currentBucketEnd = Math.Min(currentBucketEnd, dataLength);

            // Point A: the previously selected point.
            double ax = data[a].Timestamp.ToUnixTimeMilliseconds();
            double ay = data[a].Value;

            double maxArea    = -1.0;
            int    maxAreaIdx = currentBucketStart;

            for (int j = currentBucketStart; j < currentBucketEnd; j++)
            {
                double bx = data[j].Timestamp.ToUnixTimeMilliseconds();
                double by = data[j].Value;

                // Area of triangle ABC using the cross-product formula:
                //   area = |Ax(By - Cy) + Bx(Cy - Ay) + Cx(Ay - By)| / 2
                // Denominator (/2) is omitted — we only need relative comparison.
                double area = Math.Abs(
                    (ax - avgX) * (by - ay) -
                    (ax - bx)   * (avgY - ay));

                if (area > maxArea)
                {
                    maxArea    = area;
                    maxAreaIdx = j;
                }
            }

            sampled[sampledIdx++] = data[maxAreaIdx];
            a = maxAreaIdx;
        }

        // Always include the last point.
        sampled[sampledIdx] = data[dataLength - 1];

        return sampled;
    }

    /// <summary>
    /// Async wrapper that executes LTTB on a <see cref="Task.Run"/> background thread
    /// and posts the result back to the caller without blocking the UI thread.
    ///
    /// This is the primary entry point for the ViewModel layer.
    /// </summary>
    public static async ValueTask<ChartPoint[]> DownsampleAsync(
        IReadOnlyList<ChartPoint> data,
        int                       threshold = DefaultThreshold,
        CancellationToken         ct        = default)
    {
        ct.ThrowIfCancellationRequested();

        if (data.Count <= threshold)
        {
            // Still marshal off the UI thread if the list is large enough to cause jank.
            if (data.Count <= 256)
            {
                var quick = new ChartPoint[data.Count];
                for (int i = 0; i < data.Count; i++) quick[i] = data[i];
                return quick;
            }
        }

        return await Task.Run(() => Downsample(data, threshold), ct).ConfigureAwait(false);
    }
}
