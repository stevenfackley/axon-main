using System.Buffers;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using Axon.Core.Ports;
using Axon.UI.ViewModels;

namespace Axon.UI.Rendering;

/// <summary>
/// A custom Avalonia control that renders a biometric time-series using
/// direct SkiaSharp GPU canvas calls, targeting a stable 120fps render loop.
///
/// Architecture
/// ────────────
/// • Extends <see cref="Control"/> and overrides <see cref="Render"/> to push
///   an <see cref="ICustomDrawOperation"/> into Avalonia's scene graph. This
///   keeps all Skia calls on the render thread, which is separate from the
///   UI thread in Avalonia's composition model.
///
/// • Data path:
///     ViewModel.ChartPoints  ──LTTB──▶  SKPoint[] (ArrayPool)  ──▶  DrawVertices
///
/// Render Pipeline (per frame at 120fps)
/// ──────────────────────────────────────
///   1. Stress zone background: SKSL shader compiled once, uniforms updated per frame.
///   2. Line graph:             SKCanvas.DrawVertices with a triangle-strip for bulk
///                              data point rendering (single draw call regardless of N).
///   3. Anomaly markers:        Circles drawn only over flagged points.
///   4. Grid lines + labels:    Lightweight SKPaint strokes.
///
/// Memory
/// ──────
/// • SKPoint[] vertex buffers are rented from <see cref="ArrayPool{T}"/> and
///   returned immediately after the draw call — zero hot-path heap allocations.
/// • The <see cref="SKRuntimeEffect"/> (SKSL shader) is compiled once per
///   control lifetime and cached as a static field.
/// </summary>
public sealed class SkiaTelemetryChart : Control
{
    // ── Avalonia Styled Properties ─────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<ChartPoint>> PointsProperty =
        AvaloniaProperty.Register<SkiaTelemetryChart, IReadOnlyList<ChartPoint>>(
            nameof(Points),
            defaultValue: Array.Empty<ChartPoint>());

    public static readonly StyledProperty<IReadOnlyList<AnomalyResult>> AnomalyMarkersProperty =
        AvaloniaProperty.Register<SkiaTelemetryChart, IReadOnlyList<AnomalyResult>>(
            nameof(AnomalyMarkers),
            defaultValue: Array.Empty<AnomalyResult>());

    public static readonly StyledProperty<double> MinValueProperty =
        AvaloniaProperty.Register<SkiaTelemetryChart, double>(nameof(MinValue), defaultValue: 40.0);

    public static readonly StyledProperty<double> MaxValueProperty =
        AvaloniaProperty.Register<SkiaTelemetryChart, double>(nameof(MaxValue), defaultValue: 200.0);

    public static readonly StyledProperty<float> BackgroundAlphaProperty =
        AvaloniaProperty.Register<SkiaTelemetryChart, float>(nameof(BackgroundAlpha), defaultValue: 0.18f);

    // ── Properties ────────────────────────────────────────────────────────────

    /// <summary>LTTB-downsampled data points bound from DashboardViewModel.</summary>
    public IReadOnlyList<ChartPoint> Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>Anomaly markers flagged by the IID Spike engine.</summary>
    public IReadOnlyList<AnomalyResult> AnomalyMarkers
    {
        get => GetValue(AnomalyMarkersProperty);
        set => SetValue(AnomalyMarkersProperty, value);
    }

    /// <summary>Y-axis minimum value (chart bottom).</summary>
    public double MinValue
    {
        get => GetValue(MinValueProperty);
        set => SetValue(MinValueProperty, value);
    }

    /// <summary>Y-axis maximum value (chart top).</summary>
    public double MaxValue
    {
        get => GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    /// <summary>
    /// Global alpha for the stress zone background gradient (0 = invisible, 1 = opaque).
    /// Default 0.18 keeps the gradient subtle so the data line reads clearly.
    /// </summary>
    public float BackgroundAlpha
    {
        get => GetValue(BackgroundAlphaProperty);
        set => SetValue(BackgroundAlphaProperty, value);
    }

    // ── SKSL Shader (compiled once, cached for the process lifetime) ──────────

    /// <summary>
    /// The SKSL stress-zone shader source, embedded as a compile-time constant
    /// to avoid file I/O during rendering and ensure AOT compatibility.
    /// </summary>
    private static readonly string SkslShaderSource =
        """
        uniform float2 iResolution;
        uniform float  iTime;
        uniform float  zone1End;
        uniform float  zone2End;
        uniform float  zone3End;
        uniform float4 colorRecovery;
        uniform float4 colorAerobic;
        uniform float4 colorAnaerobic;
        uniform float4 colorRedLine;
        uniform float  backgroundAlpha;

        float zoneMix(float y, float boundary, float width) {
            return smoothstep(boundary - width, boundary + width, y);
        }

        half4 main(float2 fragCoord) {
            float normY   = fragCoord.y / iResolution.y;
            float feather = 0.03;

            float t12 = zoneMix(normY, zone1End, feather);
            float t23 = zoneMix(normY, zone2End, feather);
            float t34 = zoneMix(normY, zone3End, feather);

            float4 blended = mix(colorRecovery, colorAerobic,   t12);
            blended        = mix(blended,       colorAnaerobic, t23);
            blended        = mix(blended,       colorRedLine,   t34);

            float pulse    = 1.0 + 0.02 * sin(iTime * 0.8 + normY * 3.14159);
            blended.rgb   *= pulse;

            float vignette = 1.0 - 0.25 * pow(abs(normY * 2.0 - 1.0), 3.0);
            blended.rgb   *= vignette;

            blended.a = backgroundAlpha;
            return half4(blended);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect?> CachedShaderEffect =
        new(() =>
        {
            var effect = SKRuntimeEffect.Create(SkslShaderSource, out string? errors);
            if (errors is not null)
            {
                // Log but don't crash — chart degrades gracefully without the shader.
                System.Diagnostics.Debug.WriteLine($"[SkiaTelemetryChart] SKSL compile error: {errors}");
                return null;
            }
            return effect;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    // ── Elapsed time for shader animation ────────────────────────────────────
    private static readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();

    // ── Static constructor ────────────────────────────────────────────────────

    static SkiaTelemetryChart()
    {
        // Trigger invalidation whenever bound data changes.
        AffectsRender<SkiaTelemetryChart>(
            PointsProperty,
            AnomalyMarkersProperty,
            MinValueProperty,
            MaxValueProperty,
            BackgroundAlphaProperty);
    }

    // ── Avalonia Render Override ───────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        // Capture a snapshot of all state needed by the draw operation.
        // All captured values are value types or immutable references —
        // safe to read from the render thread.
        var drawOp = new TelemetryDrawOperation(
            Bounds,
            Points,
            AnomalyMarkers,
            MinValue,
            MaxValue,
            BackgroundAlpha,
            (float)_elapsed.Elapsed.TotalSeconds);

        context.Custom(drawOp);

        // Re-schedule the next frame immediately to sustain 120fps.
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    // ── Custom Draw Operation (executes on the Avalonia render thread) ────────

    private sealed class TelemetryDrawOperation : ICustomDrawOperation
    {
        // ── Design tokens ──────────────────────────────────────────────────────
        private static readonly SKColor AnomalyColor  = new(0xFF, 0x45, 0x45, 0xFF); // Alert red
        private static readonly SKColor GridLineColor = new(0xFF, 0xFF, 0xFF, 0x1A); // 10% white
        private static readonly SKColor LabelColor    = new(0xFF, 0xFF, 0xFF, 0x80); // 50% white

        // 1.5px renders as a crisp physical pixel on 2× Retina / 120Hz panels.
        private const float LineWidth     = 1.5f;
        private const float AnomalyRadius = 4.0f;
        private const int   GridLineCount = 5;

        private readonly Rect                         _bounds;
        private readonly IReadOnlyList<ChartPoint>    _points;
        private readonly IReadOnlyList<AnomalyResult> _anomalies;
        private readonly double                       _minValue;
        private readonly double                       _maxValue;
        private readonly float                        _bgAlpha;
        private readonly float                        _iTime;

        public Rect Bounds => _bounds;

        public TelemetryDrawOperation(
            Rect                         bounds,
            IReadOnlyList<ChartPoint>    points,
            IReadOnlyList<AnomalyResult> anomalies,
            double                       minValue,
            double                       maxValue,
            float                        bgAlpha,
            float                        iTime)
        {
            _bounds    = bounds;
            _points    = points;
            _anomalies = anomalies;
            _minValue  = minValue;
            _maxValue  = maxValue;
            _bgAlpha   = bgAlpha;
            _iTime     = iTime;
        }

        public bool HitTest(Point p) => _bounds.Contains(p);

        // Always return false so Avalonia never skips a frame by considering
        // two operations "equal". The 120fps loop requires every frame to redraw.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (!context.TryGetFeature<ISkiaSharpApiLeaseFeature>(out var leaseFeature))
                return;

            using var lease  = leaseFeature.Lease();
            var       canvas = lease.SkCanvas;

            float w = (float)_bounds.Width;
            float h = (float)_bounds.Height;

            if (w <= 0 || h <= 0) return;

            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, w, h));

            // ── 1. SKSL stress zone background ────────────────────────────────
            DrawBackground(canvas, w, h);

            // ── 2. Grid lines + Y-axis labels ─────────────────────────────────
            DrawGrid(canvas, w, h);

            // ── 3. Telemetry line (single DrawVertices call) ──────────────────
            if (_points.Count >= 2)
                DrawTelemetryLine(canvas, w, h);

            // ── 4. Anomaly markers (only rendered when anomalies are flagged) ──
            if (_anomalies.Count > 0)
                DrawAnomalyMarkers(canvas, w, h);

            canvas.Restore();
        }

        // ── Background (SKSL shader) ──────────────────────────────────────────

        private void DrawBackground(SKCanvas canvas, float w, float h)
        {
            var effect = CachedShaderEffect.Value;

            if (effect is null)
            {
                // Graceful degradation: solid dark background.
                canvas.Clear(new SKColor(0x0F, 0x0F, 0x1A, 0xFF));
                return;
            }

            // SKRuntimeEffectUniforms holds the uniform data in a flat byte array.
            // Values MUST be set in the same order they are declared in the SKSL source.
            // SkiaSharp 2.x uses string-indexed assignment; not IDisposable.
            var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["iResolution"]     = new[] { w, h };
            uniforms["iTime"]           = _iTime;
            uniforms["zone1End"]        = 0.50f;
            uniforms["zone2End"]        = 0.75f;
            uniforms["zone3End"]        = 0.90f;
            uniforms["colorRecovery"]   = ColorToFloat4(0x0D, 0x94, 0x88);
            uniforms["colorAerobic"]    = ColorToFloat4(0x25, 0x63, 0xEB);
            uniforms["colorAnaerobic"]  = ColorToFloat4(0xD9, 0x77, 0x06);
            uniforms["colorRedLine"]    = ColorToFloat4(0xDC, 0x26, 0x26);
            uniforms["backgroundAlpha"] = _bgAlpha;

            using var shader = effect.ToShader(false, uniforms);
            using var paint  = new SKPaint { Shader = shader };

            canvas.DrawRect(SKRect.Create(w, h), paint);
        }

        /// <summary>Converts R/G/B bytes (0-255) to a normalized float4 [r,g,b,1] array.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float[] ColorToFloat4(byte r, byte g, byte b) =>
            [r / 255f, g / 255f, b / 255f, 1.0f];

        // ── Grid Lines ────────────────────────────────────────────────────────

        private void DrawGrid(SKCanvas canvas, float w, float h)
        {
            using var linePaint = new SKPaint
            {
                Color       = GridLineColor,
                StrokeWidth = 0.5f,
                IsAntialias = false,
                Style       = SKPaintStyle.Stroke
            };

            using var labelPaint = new SKPaint
            {
                Color       = LabelColor,
                TextSize    = 11f,
                IsAntialias = true
            };

            double valueRange = _maxValue - _minValue;

            for (int i = 0; i <= GridLineCount; i++)
            {
                double normalized = (double)i / GridLineCount;
                float  y          = (float)(normalized * h);
                double value      = _maxValue - (normalized * valueRange);

                canvas.DrawLine(0, y, w, y, linePaint);

                // Offset label upward so it sits above its corresponding grid line.
                if (y > 14f)   // Don't paint off the top edge.
                    canvas.DrawText($"{value:F0}", 6f, y - 3f, labelPaint);
            }
        }

        // ── Telemetry Line via DrawVertices (single draw call) ────────────────

        /// <summary>
        /// Renders the time-series as a GPU triangle strip.
        ///
        /// Each data point i produces two vertices:
        ///   [2i]   = (x, y - halfLineWidth)   top of the thick line
        ///   [2i+1] = (x, y + halfLineWidth)   bottom of the thick line
        ///
        /// Consecutive quads share two vertices, so N points → 2N vertices →
        /// (2N-2) triangles rendered in ONE <see cref="SKCanvas.DrawVertices"/> call.
        ///
        /// Memory: both SKPoint[] and SKColor[] buffers are rented from
        /// <see cref="ArrayPool{T}"/> and returned in the finally block.
        /// </summary>
        private void DrawTelemetryLine(SKCanvas canvas, float w, float h)
        {
            int n           = _points.Count;
            int vertexCount = n * 2;

            SKPoint[] positions = ArrayPool<SKPoint>.Shared.Rent(vertexCount);
            SKColor[] colors    = ArrayPool<SKColor>.Shared.Rent(vertexCount);

            try
            {
                double timeMin  = _points[0].Timestamp.ToUnixTimeMilliseconds();
                double timeMax  = _points[n - 1].Timestamp.ToUnixTimeMilliseconds();
                double timeSpan = Math.Max(timeMax - timeMin, 1.0);
                double valSpan  = Math.Max(_maxValue - _minValue, 1.0);
                float  halfLine = LineWidth * 0.5f;

                for (int i = 0; i < n; i++)
                {
                    ChartPoint pt = _points[i];

                    float x = (float)(((pt.Timestamp.ToUnixTimeMilliseconds() - timeMin) / timeSpan) * w);
                    float y = (float)(h - ((pt.Value - _minValue) / valSpan) * h);

                    x = Math.Clamp(x, 0f, w);
                    y = Math.Clamp(y, halfLine, h - halfLine);

                    positions[i * 2]     = new SKPoint(x, y - halfLine);
                    positions[i * 2 + 1] = new SKPoint(x, y + halfLine);

                    // Per-vertex gradient: teal (low HR) → amber (mid) → red (high HR).
                    float t = Math.Clamp((float)((pt.Value - _minValue) / valSpan), 0f, 1f);
                    SKColor c = InterpolateLineColor(t);
                    colors[i * 2]     = c;
                    colors[i * 2 + 1] = c;
                }

                // CreateCopy takes managed arrays; after this call the arrays can be returned.
                var vertices = SKVertices.CreateCopy(
                    SKVertexMode.TriangleStrip,
                    positions[..vertexCount],
                    texs:   null,
                    colors: colors[..vertexCount]);

                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawVertices(vertices, SKBlendMode.SrcOver, paint);
            }
            finally
            {
                ArrayPool<SKPoint>.Shared.Return(positions, clearArray: false);
                ArrayPool<SKColor>.Shared.Return(colors,    clearArray: false);
            }
        }

        /// <summary>
        /// Perceptually smooth 3-stop color ramp for the HR line:
        ///   t = 0.0  →  Teal   (#00FFC8) — resting / recovery zone
        ///   t = 0.5  →  Amber  (#F59E0B) — aerobic zone
        ///   t = 1.0  →  Red    (#EF4444) — anaerobic / red-line zone
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SKColor InterpolateLineColor(float t)
        {
            if (t <= 0.5f)
            {
                // Teal (#00FFC8) → Amber (#F59E0B)
                float u = t * 2f;
                return new SKColor(
                    (byte)(0xF5 * u),                    // R: 0x00 → 0xF5
                    (byte)(0xFF + (0x9E - 0xFF) * u),    // G: 0xFF → 0x9E
                    (byte)(0xC8 + (0x0B - 0xC8) * u),    // B: 0xC8 → 0x0B
                    0xFF);
            }
            else
            {
                // Amber (#F59E0B) → Red (#EF4444)
                float u = (t - 0.5f) * 2f;
                return new SKColor(
                    (byte)(0xF5 + (0xEF - 0xF5) * u),   // R: 0xF5 → 0xEF
                    (byte)(0x9E - 0x9E * u),              // G: 0x9E → 0x00
                    (byte)(0x0B - 0x0B * u),              // B: 0x0B → 0x00
                    0xFF);
            }
        }

        // ── Anomaly Markers ───────────────────────────────────────────────────

        private void DrawAnomalyMarkers(SKCanvas canvas, float w, float h)
        {
            if (_points.Count < 2) return;

            double timeMin  = _points[0].Timestamp.ToUnixTimeMilliseconds();
            double timeMax  = _points[^1].Timestamp.ToUnixTimeMilliseconds();
            double timeSpan = Math.Max(timeMax - timeMin, 1.0);
            double valSpan  = Math.Max(_maxValue - _minValue, 1.0);

            using var ringPaint = new SKPaint
            {
                Color       = AnomalyColor,
                IsAntialias = true,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f
            };

            using var glowPaint = new SKPaint
            {
                Color      = AnomalyColor.WithAlpha(0x40),
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                MaskFilter  = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f)
            };

            foreach (var anomaly in _anomalies)
            {
                if (!anomaly.IsAnomaly) continue;

                long tsMs = anomaly.Timestamp.ToUnixTimeMilliseconds();
                float x = (float)(((tsMs - timeMin) / timeSpan) * w);
                float y = FindYForTimestamp(anomaly.Timestamp, h, valSpan);

                // Soft glow halo first, then crisp ring on top.
                canvas.DrawCircle(x, y, AnomalyRadius * 2f, glowPaint);
                canvas.DrawCircle(x, y, AnomalyRadius,      ringPaint);
            }
        }

        /// <summary>
        /// Binary search through the downsampled point list for the Y-canvas coordinate
        /// of the point nearest to <paramref name="ts"/>. O(log n).
        /// </summary>
        private float FindYForTimestamp(DateTimeOffset ts, float h, double valSpan)
        {
            long target = ts.ToUnixTimeMilliseconds();
            int  lo = 0, hi = _points.Count - 1;

            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_points[mid].Timestamp.ToUnixTimeMilliseconds() < target) lo = mid + 1;
                else hi = mid;
            }

            float y = (float)(h - ((_points[lo].Value - _minValue) / valSpan) * h);
            return Math.Clamp(y, 0f, h);
        }

        public void Dispose() { /* All SKPaint objects are scoped with 'using'. */ }
    }
}
