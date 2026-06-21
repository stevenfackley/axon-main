using Axon.Core.Domain;
using Axon.Infrastructure.Analytics;

namespace Axon.Tests;

/// <summary>
/// Unit tests for <see cref="TagCorrelationAnalyzer"/>.
///
/// Covers: empty inputs, tiny-sample guard, direction sign, ranking order,
/// deterministic ordering, hand-checked mean-difference, and coefficient magnitude.
/// </summary>
public sealed class TagCorrelationAnalyzerTests
{
    // ── Test fixtures ─────────────────────────────────────────────────────────

    private static readonly DateOnly _base = new(2026, 1, 1);
    private static readonly TagCorrelationAnalyzer _sut = new();

    private static Tag MakeTag(string name, string category = "supplement") =>
        new(Guid.NewGuid(), name, category);

    private static TagAnnotation Annotate(Guid tagId, DateOnly date) =>
        new(tagId, new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), null, null);

    /// <summary>Builds a daily series of <paramref name="count"/> days starting from <see cref="_base"/>.</summary>
    private static List<(DateOnly Date, double MetricValue)> MakeSeries(
        int count,
        Func<int, double>? valueFunc = null)
    {
        var list = new List<(DateOnly, double)>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add((_base.AddDays(i), valueFunc?.Invoke(i) ?? 60.0));
        }
        return list;
    }

    // ── Empty / degenerate inputs ─────────────────────────────────────────────

    [Fact]
    public void Analyze_NoAnnotations_ReturnsEmpty()
    {
        var tag = MakeTag("Caffeine");
        var series = MakeSeries(30);

        var result = _sut.Analyze(
            Array.Empty<TagAnnotation>(),
            series,
            [tag]);

        Assert.Empty(result);
    }

    [Fact]
    public void Analyze_EmptySeries_ReturnsEmpty()
    {
        var tag = MakeTag("Caffeine");
        var ann = Annotate(tag.Id, _base);

        var result = _sut.Analyze(
            [ann],
            Array.Empty<(DateOnly, double)>(),
            [tag]);

        Assert.Empty(result);
    }

    [Fact]
    public void Analyze_EmptyTags_ReturnsEmpty()
    {
        var tag = MakeTag("Caffeine");
        var series = MakeSeries(30);
        var ann = Annotate(tag.Id, _base);

        var result = _sut.Analyze(
            [ann],
            series,
            Array.Empty<Tag>());

        Assert.Empty(result);
    }

    // ── Tiny-sample guard ─────────────────────────────────────────────────────

    [Fact]
    public void Analyze_SampleSizeBelowThreshold_StrengthIsNeedsMoreData()
    {
        // 9 days total = below MinSampleSize (10)
        var tag = MakeTag("Melatonin");
        var series = MakeSeries(9, i => i < 3 ? 90.0 : 60.0);
        var annotations = Enumerable.Range(0, 3)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.Equal("Needs more data", result[0].Strength);
        Assert.Equal(9, result[0].SampleSize);
    }

    [Fact]
    public void Analyze_SampleSizeAtThreshold_StrengthIsNotNeedsMoreData()
    {
        // Exactly MinSampleSize days (10); give strong signal so we get a non-Negligible label.
        var tag = MakeTag("Ashwagandha");
        int n = TagCorrelationAnalyzer.MinSampleSize;
        // First 5 days → high value, annotated; last 5 days → low value, unannotated.
        var series = MakeSeries(n, i => i < 5 ? 90.0 : 60.0);
        var annotations = Enumerable.Range(0, 5)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.NotEqual("Needs more data", result[0].Strength);
    }

    // ── Effect direction sign ─────────────────────────────────────────────────

    [Fact]
    public void Analyze_TagOnHighDays_PositiveEffectSize()
    {
        var tag = MakeTag("Creatine");
        // 20 days; tag on first 10 where value is high.
        var series = MakeSeries(20, i => i < 10 ? 80.0 : 50.0);
        var annotations = Enumerable.Range(0, 10)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.True(result[0].EffectSize > 0, $"Expected positive effect, got {result[0].EffectSize}");
        Assert.True(result[0].Coefficient > 0, $"Expected positive coefficient, got {result[0].Coefficient}");
    }

    [Fact]
    public void Analyze_TagOnLowDays_NegativeEffectSize()
    {
        var tag = MakeTag("Alcohol");
        // 20 days; tag on first 10 where value is low.
        var series = MakeSeries(20, i => i < 10 ? 40.0 : 80.0);
        var annotations = Enumerable.Range(0, 10)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.True(result[0].EffectSize < 0, $"Expected negative effect, got {result[0].EffectSize}");
        Assert.True(result[0].Coefficient < 0, $"Expected negative coefficient, got {result[0].Coefficient}");
    }

    // ── Hand-checked mean difference ──────────────────────────────────────────

    [Fact]
    public void Analyze_MeanDifference_CalculatedCorrectly()
    {
        // 6 with-tag days (value 90), 6 without-tag days (value 60).
        // meanWith = 90, meanWithout = 60, effectSize = 30.
        var tag = MakeTag("Vitamin D");
        var series = MakeSeries(12, i => i < 6 ? 90.0 : 60.0);
        var annotations = Enumerable.Range(0, 6)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.Equal(90.0, result[0].MeanWith, precision: 6);
        Assert.Equal(60.0, result[0].MeanWithout, precision: 6);
        Assert.Equal(30.0, result[0].EffectSize, precision: 6);
    }

    // ── Ranking order ─────────────────────────────────────────────────────────

    [Fact]
    public void Analyze_StrongAssociationRanksAboveWeak()
    {
        var strongTag = MakeTag("StrongTag");
        var weakTag = MakeTag("WeakTag");
        int n = 30;

        // StrongTag: first 15 days value 90, else 50 → big delta.
        // WeakTag: first 15 days value 62, else 58 → small delta.
        var series = MakeSeries(n, i => 0.0); // placeholder — overridden per annotation

        // Build a shared series where we can test both tags simultaneously.
        // Use a series of 30 days with values that make each effect clear.
        var sharedSeries = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            sharedSeries.Add((_base.AddDays(i), i < 15 ? 90.0 : 50.0));
        }

        // StrongTag annotated on the high-value days (0..14)
        var strongAnnotations = Enumerable.Range(0, 15)
            .Select(i => Annotate(strongTag.Id, _base.AddDays(i)))
            .ToArray();

        // WeakTag annotated on days 0..14 but the series values differ by only 4.
        var weakSeries = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            weakSeries.Add((_base.AddDays(i), i < 15 ? 62.0 : 58.0));
        }
        var weakAnnotations = Enumerable.Range(0, 15)
            .Select(i => Annotate(weakTag.Id, _base.AddDays(i)))
            .ToArray();

        // Analyze them separately and compare effect sizes.
        var strongResult = _sut.Analyze(strongAnnotations, sharedSeries, [strongTag]);
        var weakResult = _sut.Analyze(weakAnnotations, weakSeries, [weakTag]);

        Assert.Single(strongResult);
        Assert.Single(weakResult);
        Assert.True(
            Math.Abs(strongResult[0].EffectSize) > Math.Abs(weakResult[0].EffectSize),
            $"Strong effect {strongResult[0].EffectSize} should exceed weak effect {weakResult[0].EffectSize}");
    }

    [Fact]
    public void Analyze_MultipleTagsSortedByAbsoluteEffect()
    {
        var tagA = MakeTag("TagA");
        var tagB = MakeTag("TagB");
        var tagC = MakeTag("TagC");

        int n = 30;
        // Series where tag presence = higher value; tags have different deltas.
        var series = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            series.Add((_base.AddDays(i), 60.0)); // flat baseline
        }

        // Annotate: tagA on 0..9 (strong signal injected into series via separate effect)
        // Actually: let's use different series to keep tags independent — but analyzer takes
        // one series. So build a series that gives each tag proportional contributions.
        // Simpler approach: use distinct series per test, but here we test multi-tag ranking
        // via a combined annotation set with a series designed so tagA effect > tagB > tagC.

        // Series has 30 days. Days 0-9 have value 90, days 10-19 have value 70, days 20-29 have value 50.
        var gradedSeries = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            double val = i < 10 ? 90.0 : (i < 20 ? 70.0 : 50.0);
            gradedSeries.Add((_base.AddDays(i), val));
        }

        // tagA: days 0-9 only (mean-with=90, mean-without=60 → effect +30)
        var annotationsA = Enumerable.Range(0, 10).Select(i => Annotate(tagA.Id, _base.AddDays(i))).ToArray();

        // tagB: days 10-19 only (mean-with=70, mean-without approx 70 → effect near 0 from symmetry)
        // Actually mean-without = (90*10+50*10)/20 = 70. So effectB = 70-70 = 0. Fine — it's weakest.
        var annotationsB = Enumerable.Range(10, 10).Select(i => Annotate(tagB.Id, _base.AddDays(i))).ToArray();

        // tagC: days 20-29 only (mean-with=50, mean-without=(90*10+70*10)/20=80 → effect= -30)
        var annotationsC = Enumerable.Range(20, 10).Select(i => Annotate(tagC.Id, _base.AddDays(i))).ToArray();

        var allAnnotations = annotationsA.Concat(annotationsB).Concat(annotationsC).ToArray();
        var allTags = new[] { tagA, tagB, tagC };

        var result = _sut.Analyze(allAnnotations, gradedSeries, allTags);

        Assert.Equal(3, result.Count);

        // |effect| descending: tagA (+30) and tagC (-30) tie → secondary sort by name;
        // tagB (0) comes last.
        double absFirst = Math.Abs(result[0].EffectSize);
        double absSecond = Math.Abs(result[1].EffectSize);
        double absThird = Math.Abs(result[2].EffectSize);

        Assert.True(absFirst >= absSecond, $"First |effect|={absFirst} not >= second |effect|={absSecond}");
        Assert.True(absSecond >= absThird, $"Second |effect|={absSecond} not >= third |effect|={absThird}");
        Assert.Equal("TagB", result[^1].TagName); // weakest effect
    }

    // ── Deterministic ordering ────────────────────────────────────────────────

    [Fact]
    public void Analyze_SameInputTwice_ProducesSameOrder()
    {
        var tagA = MakeTag("Apple");
        var tagB = MakeTag("Banana");
        var tagC = MakeTag("Cherry");

        var series = MakeSeries(30, i => i * 2.0);
        var annotations = new[]
        {
            Annotate(tagA.Id, _base.AddDays(0)),
            Annotate(tagA.Id, _base.AddDays(2)),
            Annotate(tagB.Id, _base.AddDays(1)),
            Annotate(tagB.Id, _base.AddDays(3)),
            Annotate(tagC.Id, _base.AddDays(5)),
        };

        var first = _sut.Analyze(annotations, series, [tagA, tagB, tagC]);
        var second = _sut.Analyze(annotations, series, [tagA, tagB, tagC]);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].TagName, second[i].TagName);
            Assert.Equal(first[i].EffectSize, second[i].EffectSize);
        }
    }

    // ── Coefficient sanity ────────────────────────────────────────────────────

    [Fact]
    public void Analyze_PerfectBinaryCorrelation_CoefficientNearPlusOne()
    {
        // Tag on exactly the high-value days with no noise → strong positive r.
        var tag = MakeTag("Signal");
        int n = 20;
        var series = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            series.Add((_base.AddDays(i), i < 10 ? 100.0 : 0.0));
        }
        var annotations = Enumerable.Range(0, 10)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.True(result[0].Coefficient > 0.8,
            $"Expected coefficient near +1, got {result[0].Coefficient}");
    }

    [Fact]
    public void Analyze_PerfectInverseCorrelation_CoefficientNearMinusOne()
    {
        var tag = MakeTag("Inhibitor");
        int n = 20;
        var series = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            series.Add((_base.AddDays(i), i < 10 ? 0.0 : 100.0));
        }
        var annotations = Enumerable.Range(0, 10)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.True(result[0].Coefficient < -0.8,
            $"Expected coefficient near -1, got {result[0].Coefficient}");
    }

    [Fact]
    public void Analyze_FlatSeries_CoefficientIsZero()
    {
        var tag = MakeTag("Placebo");
        var series = MakeSeries(20, _ => 75.0); // completely flat
        var annotations = Enumerable.Range(0, 10)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        Assert.Equal(0.0, result[0].Coefficient, precision: 10);
    }

    // ── Annotation deduplication ──────────────────────────────────────────────

    [Fact]
    public void Analyze_MultipleAnnotationsSameDay_CountsOncePerDay()
    {
        var tag = MakeTag("Caffeine");
        // Annotate day 0 three times (e.g. morning/afternoon/evening dose).
        var annotations = new[]
        {
            new TagAnnotation(tag.Id, new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), 100, null),
            new TagAnnotation(tag.Id, new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero), 50, null),
            new TagAnnotation(tag.Id, new DateTimeOffset(2026, 1, 1, 17, 0, 0, TimeSpan.Zero), 80, null),
        };
        var series = MakeSeries(10, _ => 65.0);

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Single(result);
        // SampleSize = total days in the series, not annotation count.
        Assert.Equal(10, result[0].SampleSize);
    }

    // ── Strength labels ───────────────────────────────────────────────────────

    [Fact]
    public void Analyze_StrongCoefficient_StrengthIsStrong()
    {
        var tag = MakeTag("StrongSignal");
        int n = 30;
        // Perfect step: first half high, second half low.
        var series = new List<(DateOnly, double)>(n);
        for (int i = 0; i < n; i++)
        {
            series.Add((_base.AddDays(i), i < 15 ? 100.0 : 0.0));
        }
        var annotations = Enumerable.Range(0, 15)
            .Select(i => Annotate(tag.Id, _base.AddDays(i)))
            .ToArray();

        var result = _sut.Analyze(annotations, series, [tag]);

        Assert.Equal("Strong", result[0].Strength);
    }
}
