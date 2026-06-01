using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TinyChaos.Gui;

/// <summary>
/// Interactive viewer for a whole saved <see cref="CaptureBuffer"/>: scroll,
/// horizontal zoom, and an adjustable vertical range so you can inspect small
/// signal changes (e.g. set the Y axis to 22000..23000 instead of 0..65535).
///
/// Interaction:
///   - Left-drag            pan (horizontal = time, vertical = value)
///   - Mouse wheel          zoom the time axis about the cursor
///   - Shift + mouse wheel   zoom the value axis about the cursor
///   - <see cref="ResetView"/> (Reset button) returns to the full capture.
///
/// The viewport is (XOffset, XSpan) in sample units on X and (YMin, YMax) on Y.
/// Rendering decimates with a per-pixel min/max envelope when more samples are
/// visible than pixels, so even very large captures draw fast and without
/// aliasing; it switches to a point-to-point line when zoomed in far enough.
/// </summary>
public sealed class SavedWaveformView : Control
{
    private const double LeftAxisWidth = 52;
    private const double BottomAxisHeight = 18;
    private const double Padding = 6;
    private const double MinSpanSamples = 8;     // closest horizontal zoom

    private static readonly IBrush[] ChannelBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x40)),
        new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
        new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)),
    };
    private static readonly IBrush AxisTextBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xC0, 0xC8, 0xD8));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GridPen = new Pen(GridBrush, 0.5);
    private static readonly Typeface AxisTypeface = new("Menlo,Consolas,monospace", FontStyle.Normal, FontWeight.Normal);

    // ---- Bindable properties -------------------------------------------------

    public static readonly StyledProperty<CaptureBuffer?> BufferProperty =
        AvaloniaProperty.Register<SavedWaveformView, CaptureBuffer?>(nameof(Buffer));

    /// <summary>Bottom of the visible value range (Y axis). Bindable (two-way).</summary>
    public static readonly StyledProperty<double> YMinProperty =
        AvaloniaProperty.Register<SavedWaveformView, double>(nameof(YMin), 0,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Top of the visible value range (Y axis). Bindable (two-way).</summary>
    public static readonly StyledProperty<double> YMaxProperty =
        AvaloniaProperty.Register<SavedWaveformView, double>(nameof(YMax), 4095,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Status line (e.g. "samples 1000-2000 of 25600"). Read-only-ish, bindable out.</summary>
    public static readonly StyledProperty<string> RangeTextProperty =
        AvaloniaProperty.Register<SavedWaveformView, string>(nameof(RangeText), "");

    public CaptureBuffer? Buffer
    {
        get => GetValue(BufferProperty);
        set => SetValue(BufferProperty, value);
    }

    public double YMin
    {
        get => GetValue(YMinProperty);
        set => SetValue(YMinProperty, value);
    }

    public double YMax
    {
        get => GetValue(YMaxProperty);
        set => SetValue(YMaxProperty, value);
    }

    public string RangeText
    {
        get => GetValue(RangeTextProperty);
        private set => SetValue(RangeTextProperty, value);
    }

    // ---- Horizontal viewport (sample units) ---------------------------------
    private double _xOffset;   // first visible sample (fractional)
    private double _xSpan;     // visible sample count

    // ---- Drag state ----------------------------------------------------------
    private bool _dragging;
    private Point _dragStart;
    private double _dragXOffset, _dragYMin, _dragYMax;

    static SavedWaveformView()
    {
        AffectsRender<SavedWaveformView>(BufferProperty, YMinProperty, YMaxProperty);
        BufferProperty.Changed.AddClassHandler<SavedWaveformView>((v, _) => v.ResetView());
    }

    public SavedWaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>Reset to the full capture, full ADC range (or a sensible default).</summary>
    public void ResetView()
    {
        var buf = Buffer;
        _xOffset = 0;
        _xSpan = buf is { Length: > 1 } ? buf.Length : 1;
        YMin = 0;
        YMax = 4095;  // 12-bit default; vertical zoom/inputs can widen to 65535
        UpdateRangeText();
        InvalidateVisual();
    }

    private void UpdateRangeText()
    {
        var buf = Buffer;
        if (buf is null || buf.Length == 0) { RangeText = "no data"; return; }
        long a = (long)Math.Max(0, Math.Floor(_xOffset));
        long b = (long)Math.Min(buf.Length, Math.Ceiling(_xOffset + _xSpan));
        RangeText = $"samples {a:N0}-{b:N0} of {buf.Length:N0}   y {YMin:N0}..{YMax:N0}";
    }

    private void ClampX()
    {
        var buf = Buffer;
        int len = buf?.Length ?? 1;
        _xSpan = Math.Clamp(_xSpan, MinSpanSamples, Math.Max(MinSpanSamples, len));
        _xOffset = Math.Clamp(_xOffset, 0, Math.Max(0, len - _xSpan));
    }

    // ---- Interaction ---------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Buffer is null) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragXOffset = _xOffset;
        _dragYMin = YMin;
        _dragYMax = YMax;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var plot = PlotRect();
        if (plot.Width <= 0 || plot.Height <= 0) return;

        var p = e.GetPosition(this);
        double dx = p.X - _dragStart.X;
        double dy = p.Y - _dragStart.Y;

        // Horizontal: grab-and-move - dragging right reveals earlier samples.
        _xOffset = _dragXOffset - dx / plot.Width * _xSpan;
        ClampX();

        // Vertical: dragging down moves the value window up (content follows cursor).
        double yRange = _dragYMax - _dragYMin;
        double dValue = dy / plot.Height * yRange;
        YMin = _dragYMin + dValue;
        YMax = _dragYMax + dValue;

        UpdateRangeText();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Buffer is null) return;
        var plot = PlotRect();
        if (plot.Width <= 0 || plot.Height <= 0) return;

        // Wheel up = zoom in (shrink the span); down = zoom out.
        double factor = e.Delta.Y > 0 ? 1.0 / 1.25 : 1.25;
        var p = e.GetPosition(this);
        bool vertical = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (vertical)
        {
            // Zoom the value axis about the cursor's value.
            double frac = Math.Clamp((plot.Bottom - p.Y) / plot.Height, 0, 1);
            double range = YMax - YMin;
            double cursorVal = YMin + frac * range;
            double newRange = Math.Max(1, range * factor);
            YMin = cursorVal - frac * newRange;
            YMax = cursorVal + (1 - frac) * newRange;
        }
        else
        {
            // Zoom the time axis about the cursor's sample.
            double frac = Math.Clamp((p.X - plot.Left) / plot.Width, 0, 1);
            double cursorSample = _xOffset + frac * _xSpan;
            _xSpan = _xSpan * factor;
            ClampX();
            _xOffset = cursorSample - frac * _xSpan;
            ClampX();
        }

        UpdateRangeText();
        InvalidateVisual();
        e.Handled = true;
    }

    private Rect PlotRect()
    {
        var b = Bounds;
        double w = b.Width - LeftAxisWidth - Padding;
        double h = b.Height - BottomAxisHeight - Padding;
        return new Rect(LeftAxisWidth, Padding, Math.Max(0, w), Math.Max(0, h));
    }

    // ---- Rendering -----------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        var plot = PlotRect();
        if (plot.Width <= 1 || plot.Height <= 1) return;

        DrawAxes(context, plot);

        var buf = Buffer;
        if (buf is null || buf.Length < 2 || _xSpan <= 0) return;

        double yMin = YMin, yMax = YMax;
        if (yMax - yMin < 1e-6) yMax = yMin + 1;   // guard
        double yScale = plot.Height / (yMax - yMin);

        for (int ch = 0; ch < buf.ChannelCount; ch++)
        {
            DrawChannel(context, plot, buf.Channel(ch), ChannelBrushes[ch % ChannelBrushes.Length], yMin, yScale);
        }
    }

    private void DrawChannel(DrawingContext context, Rect plot, ushort[] data, IBrush brush, double yMin, double yScale)
    {
        int start = Math.Max(0, (int)Math.Floor(_xOffset));
        int end = Math.Min(data.Length, (int)Math.Ceiling(_xOffset + _xSpan));
        if (end - start < 2) return;

        double Y(double v) => plot.Bottom - (v - yMin) * yScale;
        double X(double s) => plot.Left + (s - _xOffset) / _xSpan * plot.Width;

        int pixels = (int)Math.Ceiling(plot.Width);
        var pen = new Pen(brush, 1.0);

        if ((end - start) <= pixels * 2)
        {
            // Sparse enough: draw point-to-point.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(X(start), Y(data[start])), false);
                for (int i = start + 1; i < end; i++)
                {
                    g.LineTo(new Point(X(i), Y(data[i])));
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geo);
        }
        else
        {
            // Dense: per-pixel-column min/max envelope (fast + alias-free).
            double samplesPerPixel = _xSpan / plot.Width;
            var envPen = new Pen(brush, 1.0);
            for (int px = 0; px < pixels; px++)
            {
                int s0 = (int)Math.Floor(_xOffset + px * samplesPerPixel);
                int s1 = (int)Math.Floor(_xOffset + (px + 1) * samplesPerPixel);
                s0 = Math.Clamp(s0, start, end - 1);
                s1 = Math.Clamp(s1, s0 + 1, end);
                ushort lo = data[s0], hi = data[s0];
                for (int i = s0 + 1; i < s1; i++)
                {
                    ushort v = data[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                double x = plot.Left + px + 0.5;
                context.DrawLine(envPen, new Point(x, Y(hi)), new Point(x, Y(lo)));
            }
        }
    }

    private void DrawAxes(DrawingContext context, Rect plot)
    {
        double yMin = YMin, yMax = YMax;
        if (yMax - yMin < 1e-6) yMax = yMin + 1;

        // Five horizontal gridlines + value labels at the current Y range.
        for (int k = 0; k <= 4; k++)
        {
            double frac = k / 4.0;
            double y = plot.Bottom - frac * plot.Height;
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            double val = yMin + frac * (yMax - yMin);
            var label = new FormattedText(
                ((long)Math.Round(val)).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, AxisTypeface, 10, AxisTextBrush);
            context.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }
    }
}
