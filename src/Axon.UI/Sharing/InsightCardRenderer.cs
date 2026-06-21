using SkiaSharp;

namespace Axon.UI.Sharing;

/// <summary>
/// Renders a dark-themed shareable insight card to PNG bytes.
///
/// <para>
/// Output is fully <b>deterministic</b>: identical <see cref="InsightCard"/>
/// inputs always produce byte-for-byte identical PNG output regardless of
/// when or how many times <see cref="RenderPng"/> is called.
/// </para>
///
/// <para>
/// The renderer is AOT-safe: it uses only SkiaSharp value types and avoids
/// any reflection, dynamic dispatch, or runtime code generation beyond what
/// SkiaSharp itself requires.
/// </para>
///
/// Layout (top → bottom, all measurements in logical pixels):
/// <code>
///  ┌──────────────────────────────────────────┐
///  │  [padding]  TITLE (small caps)           │
///  │             HEADLINE (large stat)         │
///  │             SUBTITLE (optional, small)    │
///  │  ─────────────────────────────────────── │
///  │             watermark                     │
///  └──────────────────────────────────────────┘
/// </code>
/// </summary>
public sealed class InsightCardRenderer
{
    // ── Design tokens ──────────────────────────────────────────────────────────

    // Background: very dark navy (#0F0F1A)
    private static readonly SKColor BackgroundColor = new(0x0F, 0x0F, 0x1A, 0xFF);

    // Accent gradient: teal (#00FFC8) → cobalt (#2563EB)
    private static readonly SKColor AccentStart = new(0x00, 0xFF, 0xC8, 0xFF);
    private static readonly SKColor AccentEnd = new(0x25, 0x63, 0xEB, 0xFF);

    // Text hierarchy
    private static readonly SKColor TitleColor = new(0xFF, 0xFF, 0xFF, 0x99);     // 60 % white
    private static readonly SKColor HeadlineColor = new(0xFF, 0xFF, 0xFF, 0xFF);  // 100 % white
    private static readonly SKColor SubtitleColor = new(0xFF, 0xFF, 0xFF, 0xB2);  // 70 % white
    private static readonly SKColor WatermarkColor = new(0xFF, 0xFF, 0xFF, 0x59); // 35 % white

    // Separator line
    private static readonly SKColor SeparatorColor = new(0xFF, 0xFF, 0xFF, 0x26); // 15 % white

    // Padding & typography ratios (relative to card height)
    private const float PaddingRatio = 0.076f;       // ~48 px at 630 h
    private const float TitleSizeRatio = 0.030f;     // ~19 px
    private const float HeadlineSizeRatio = 0.127f;  // ~80 px
    private const float SubtitleSizeRatio = 0.038f;  // ~24 px
    private const float WatermarkSizeRatio = 0.026f; // ~16 px
    private const float AccentBarHeight = 4f;        // accent bar beneath title

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders <paramref name="card"/> to a PNG byte array.
    /// </summary>
    /// <param name="card">The card content to render.</param>
    /// <param name="width">Canvas width in pixels. Default 1200.</param>
    /// <param name="height">Canvas height in pixels. Default 630.</param>
    /// <returns>PNG-encoded bytes starting with the standard PNG magic bytes.</returns>
    public byte[] RenderPng(InsightCard card, int width = 1200, int height = 630)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        float w = width;
        float h = height;
        float pad = h * PaddingRatio;

        // ── 1. Dark background ────────────────────────────────────────────────
        canvas.Clear(BackgroundColor);

        // ── 2. Subtle diagonal gradient overlay ───────────────────────────────
        DrawGradientOverlay(canvas, w, h);

        // ── 3. Accent bar (top-left corner strip) ────────────────────────────
        DrawAccentBar(canvas, w, pad);

        // ── 4. Text content ───────────────────────────────────────────────────
        float titleSize = h * TitleSizeRatio;
        float headlineSize = h * HeadlineSizeRatio;
        float subtitleSize = h * SubtitleSizeRatio;
        float watermarkSize = h * WatermarkSizeRatio;

        float x = pad;
        float y = pad + titleSize + AccentBarHeight + 12f;

        // Title (eyebrow, uppercased for small-caps effect)
        using (var titleFont = new SKFont { Size = titleSize })
        using (var titlePaint = new SKPaint { Color = TitleColor, IsAntialias = true })
        {
            canvas.DrawText(card.Title.ToUpperInvariant(), x, y, SKTextAlign.Left, titleFont, titlePaint);
        }

        y += headlineSize * 0.18f + 12f;

        // Headline (large stat)
        using (var headlineFont = new SKFont { Size = headlineSize, Embolden = true })
        using (var headlinePaint = new SKPaint { Color = HeadlineColor, IsAntialias = true })
        {
            canvas.DrawText(card.Headline, x, y + headlineSize * 0.82f, SKTextAlign.Left, headlineFont, headlinePaint);
        }

        y += headlineSize + 16f;

        // Subtitle (optional)
        if (card.Subtitle is not null)
        {
            using var subtitleFont = new SKFont { Size = subtitleSize };
            using var subtitlePaint = new SKPaint { Color = SubtitleColor, IsAntialias = true };
            canvas.DrawText(card.Subtitle, x, y, SKTextAlign.Left, subtitleFont, subtitlePaint);
        }

        // ── 5. Separator line ─────────────────────────────────────────────────
        float sepY = h - pad - watermarkSize - 16f;
        using (var sepPaint = new SKPaint { Color = SeparatorColor, StrokeWidth = 1f, IsAntialias = false, Style = SKPaintStyle.Stroke })
        {
            canvas.DrawLine(x, sepY, w - x, sepY, sepPaint);
        }

        // ── 6. Watermark footer ───────────────────────────────────────────────
        using (var wmFont = new SKFont { Size = watermarkSize })
        using (var wmPaint = new SKPaint { Color = WatermarkColor, IsAntialias = true })
        {
            canvas.DrawText(card.Watermark, x, h - pad, SKTextAlign.Left, wmFont, wmPaint);
        }

        // ── 7. Axon logo mark (simple geometric accent, top-right) ───────────
        DrawLogoMark(canvas, w, pad);

        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Draws a very subtle bottom-right diagonal gradient overlay to add depth.
    /// Uses a fixed linear gradient from transparent to a slight teal tint so the
    /// output is fully deterministic.
    /// </summary>
    private static void DrawGradientOverlay(SKCanvas canvas, float w, float h)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 0f),
            new SKPoint(w, h),
            [new SKColor(0x00, 0xFF, 0xC8, 0x10), new SKColor(0x25, 0x63, 0xEB, 0x18)],
            null,
            SKShaderTileMode.Clamp);

        using var paint = new SKPaint { Shader = shader, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(w, h), paint);
    }

    /// <summary>
    /// Draws a short accent gradient bar anchored to the left padding edge,
    /// positioned in the top-left corner.
    /// </summary>
    private static void DrawAccentBar(SKCanvas canvas, float w, float pad)
    {
        float barW = w * 0.12f; // 12 % of card width
        float barY = pad;

        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(pad, barY),
            new SKPoint(pad + barW, barY),
            [AccentStart, AccentEnd],
            null,
            SKShaderTileMode.Clamp);

        using var paint = new SKPaint { Shader = shader, IsAntialias = false };
        canvas.DrawRect(new SKRect(pad, barY, pad + barW, barY + AccentBarHeight), paint);
    }

    /// <summary>
    /// Draws a minimal geometric mark (two concentric circles, accent-coloured)
    /// in the top-right corner as a recognisable brand anchor.
    /// </summary>
    private static void DrawLogoMark(SKCanvas canvas, float w, float pad)
    {
        float cx = w - pad - 20f;
        float cy = pad + 20f;

        using var outerPaint = new SKPaint
        {
            Color = AccentStart.WithAlpha(0x50),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };

        using var innerPaint = new SKPaint
        {
            Color = AccentStart.WithAlpha(0x99),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        canvas.DrawCircle(cx, cy, 18f, outerPaint);
        canvas.DrawCircle(cx, cy, 6f, innerPaint);
    }
}
