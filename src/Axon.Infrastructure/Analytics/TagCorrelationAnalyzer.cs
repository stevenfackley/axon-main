using Axon.Core.Domain;

namespace Axon.Infrastructure.Analytics;

/// <summary>
/// Pure stateless analyzer that correlates user-defined <see cref="Tag"/> annotations
/// against a daily biometric series.
///
/// Algorithm:
///   For each tag, day-observations are split into two groups:
///   "with-tag" days (any annotation whose date matches) and "without-tag" days.
///   A point-biserial correlation coefficient is computed from the binary group
///   membership and the continuous metric values.  Results are returned sorted
///   descending by |EffectSize|.
///
/// No I/O, no EF, no DI — intentionally injectable by a ViewModel or a facade.
/// </summary>
public sealed class TagCorrelationAnalyzer
{
    /// <summary>
    /// Minimum observation count required before a coefficient is considered reliable.
    /// Below this threshold the Strength field reads "Needs more data".
    /// </summary>
    public const int MinSampleSize = 10;

    /// <summary>
    /// Computes per-tag correlations and returns results sorted by |effect| descending.
    /// </summary>
    /// <param name="annotations">
    ///     All tag annotations to consider.  Multiple annotations on the same calendar
    ///     date count as a single "with-tag" day for that tag.
    /// </param>
    /// <param name="dailySeries">
    ///     The biometric daily series: one (date, value) tuple per day.
    ///     Duplicate dates are reduced to their first occurrence.
    /// </param>
    /// <param name="tags">
    ///     The tag definitions providing display names.  Only tags that appear in
    ///     <paramref name="annotations"/> produce results.
    /// </param>
    /// <returns>
    ///     Ranked list of <see cref="TagCorrelationResult"/>, highest |effect| first.
    ///     Empty when either input collection is empty.
    /// </returns>
    public IReadOnlyList<TagCorrelationResult> Analyze(
        IReadOnlyList<TagAnnotation> annotations,
        IReadOnlyList<(DateOnly Date, double MetricValue)> dailySeries,
        IReadOnlyList<Tag> tags)
    {
        if (annotations.Count == 0 || dailySeries.Count == 0 || tags.Count == 0)
        {
            return Array.Empty<TagCorrelationResult>();
        }

        // Build a fast name lookup by TagId.
        var nameById = new Dictionary<Guid, string>(tags.Count);
        foreach (var tag in tags)
        {
            nameById[tag.Id] = tag.Name;
        }

        // Deduplicate the daily series — keep first occurrence per date.
        var seriesMap = new Dictionary<DateOnly, double>(dailySeries.Count);
        foreach (var (date, value) in dailySeries)
        {
            seriesMap.TryAdd(date, value);
        }

        var allDates = seriesMap.Keys.ToArray();
        int n = allDates.Length;
        if (n == 0)
        {
            return Array.Empty<TagCorrelationResult>();
        }

        // Group annotations by TagId → set of calendar dates.
        var tagDates = new Dictionary<Guid, HashSet<DateOnly>>();
        foreach (var annotation in annotations)
        {
            var date = DateOnly.FromDateTime(annotation.Timestamp.UtcDateTime);
            if (!tagDates.TryGetValue(annotation.TagId, out var dateSet))
            {
                dateSet = new HashSet<DateOnly>();
                tagDates[annotation.TagId] = dateSet;
            }
            dateSet.Add(date);
        }

        var results = new List<TagCorrelationResult>(tagDates.Count);

        foreach (var (tagId, taggedDates) in tagDates)
        {
            if (!nameById.TryGetValue(tagId, out string? tagName))
            {
                continue;
            }

            // Build parallel arrays: x[i] ∈ {0.0, 1.0} and y[i] = metric value.
            // Only include days that appear in the series.
            var xList = new List<double>(n);
            var yList = new List<double>(n);

            foreach (var date in allDates)
            {
                xList.Add(taggedDates.Contains(date) ? 1.0 : 0.0);
                yList.Add(seriesMap[date]);
            }

            int sampleSize = xList.Count;

            // Count with/without groups.
            double sumWith = 0.0;
            int countWith = 0;
            double sumWithout = 0.0;
            int countWithout = 0;

            for (int i = 0; i < sampleSize; i++)
            {
                if (xList[i] > 0.5)
                {
                    sumWith += yList[i];
                    countWith++;
                }
                else
                {
                    sumWithout += yList[i];
                    countWithout++;
                }
            }

            double meanWith = countWith > 0 ? sumWith / countWith : 0.0;
            double meanWithout = countWithout > 0 ? sumWithout / countWithout : 0.0;
            double effectSize = meanWith - meanWithout;

            double coefficient = ComputePointBiserial(xList, yList);
            string strength = DeriveStrength(coefficient, sampleSize);

            results.Add(new TagCorrelationResult(
                TagName: tagName,
                MeanWith: meanWith,
                MeanWithout: meanWithout,
                EffectSize: effectSize,
                Coefficient: coefficient,
                SampleSize: sampleSize,
                Strength: strength));
        }

        // Sort descending by |EffectSize|; stable secondary sort by TagName for determinism.
        results.Sort((a, b) =>
        {
            int cmp = Math.Abs(b.EffectSize).CompareTo(Math.Abs(a.EffectSize));
            return cmp != 0 ? cmp : string.Compare(a.TagName, b.TagName, StringComparison.Ordinal);
        });

        return results;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Point-biserial correlation coefficient.
    /// Mathematically equivalent to Pearson r when X is binary {0,1}.
    /// Returns 0 when variance is absent.
    /// </summary>
    private static double ComputePointBiserial(List<double> x, List<double> y)
    {
        int n = x.Count;
        if (n < 2)
        {
            return 0.0;
        }

        double meanX = 0.0;
        double meanY = 0.0;
        for (int i = 0; i < n; i++)
        {
            meanX += x[i];
            meanY += y[i];
        }
        meanX /= n;
        meanY /= n;

        double sumXY = 0.0;
        double sumXX = 0.0;
        double sumYY = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            sumXY += dx * dy;
            sumXX += dx * dx;
            sumYY += dy * dy;
        }

        if (sumXX <= double.Epsilon || sumYY <= double.Epsilon)
        {
            return 0.0;
        }

        return Math.Clamp(sumXY / Math.Sqrt(sumXX * sumYY), -1.0, 1.0);
    }

    private static string DeriveStrength(double coefficient, int sampleSize)
    {
        if (sampleSize < MinSampleSize)
        {
            return "Needs more data";
        }

        double abs = Math.Abs(coefficient);
        if (abs >= 0.5) return "Strong";
        if (abs >= 0.3) return "Moderate";
        if (abs >= 0.1) return "Weak";
        return "Negligible";
    }
}
