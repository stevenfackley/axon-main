using Axon.UI.Sharing;
using SkiaSharp;

namespace Axon.Tests;

/// <summary>
/// Unit tests for <see cref="InsightCardRenderer"/>.
///
/// Covers: non-empty output, valid PNG magic bytes, correct dimensions on decode,
/// and deterministic output (identical bytes for identical input).
/// </summary>
public sealed class InsightCardRendererTests
{
    private static readonly InsightCard DefaultCard = new(
        Title: "Weekly Summary",
        Headline: "12,847 steps",
        Subtitle: "Personal best this month",
        Watermark: "Analyzed locally by Axon");

    private readonly InsightCardRenderer _renderer = new();

    // ── Non-empty ─────────────────────────────────────────────────────────────

    [Fact]
    public void RenderPng_DefaultCard_ReturnsByteArray()
    {
        byte[] png = _renderer.RenderPng(DefaultCard);

        Assert.NotNull(png);
        Assert.NotEmpty(png);
    }

    // ── PNG magic bytes ───────────────────────────────────────────────────────

    [Fact]
    public void RenderPng_DefaultCard_StartsWithPngSignature()
    {
        byte[] png = _renderer.RenderPng(DefaultCard);

        // PNG magic: 0x89 0x50 0x4E 0x47  (\x89PNG)
        Assert.True(png.Length >= 4, "PNG must be at least 4 bytes");
        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
    }

    // ── Correct dimensions ────────────────────────────────────────────────────

    [Fact]
    public void RenderPng_DefaultDimensions_DecodesTo1200x630()
    {
        byte[] png = _renderer.RenderPng(DefaultCard);

        using var bitmap = SKBitmap.Decode(png);

        Assert.NotNull(bitmap);
        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(630, bitmap.Height);
    }

    [Fact]
    public void RenderPng_CustomDimensions_DecodesToRequestedSize()
    {
        byte[] png = _renderer.RenderPng(DefaultCard, width: 800, height: 400);

        using var bitmap = SKBitmap.Decode(png);

        Assert.NotNull(bitmap);
        Assert.Equal(800, bitmap.Width);
        Assert.Equal(400, bitmap.Height);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void RenderPng_SameCardTwice_ProducesIdenticalBytes()
    {
        byte[] first = _renderer.RenderPng(DefaultCard);
        byte[] second = _renderer.RenderPng(DefaultCard);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RenderPng_SameCardDifferentRendererInstances_ProducesIdenticalBytes()
    {
        var rendererA = new InsightCardRenderer();
        var rendererB = new InsightCardRenderer();

        byte[] a = rendererA.RenderPng(DefaultCard);
        byte[] b = rendererB.RenderPng(DefaultCard);

        Assert.Equal(a, b);
    }

    // ── Null / optional fields ─────────────────────────────────────────────────

    [Fact]
    public void RenderPng_NullSubtitle_DoesNotThrow()
    {
        var card = new InsightCard(
            Title: "Resting HR",
            Headline: "52 bpm",
            Subtitle: null);

        byte[] png = _renderer.RenderPng(card);

        Assert.NotEmpty(png);
    }

    [Fact]
    public void RenderPng_NullSubtitle_StartsWithPngSignature()
    {
        var card = new InsightCard(
            Title: "Resting HR",
            Headline: "52 bpm",
            Subtitle: null);

        byte[] png = _renderer.RenderPng(card);

        Assert.Equal(0x89, png[0]);
        Assert.Equal(0x50, png[1]);
        Assert.Equal(0x4E, png[2]);
        Assert.Equal(0x47, png[3]);
    }
}
