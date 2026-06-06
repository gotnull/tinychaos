using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Live waveform canvas. Polls its model on a 30 Hz dispatcher timer and
/// renders the per-channel samples as line traces over a faint reference grid.
/// The Y axis spans the full ADC code range, set by <see cref="FullScale"/>
/// (4096 for 12-bit, 65536 for 16-bit); tick labels are derived from it.
/// </summary>
public sealed class WaveformView : Control
{
    /// <summary>Full-scale code count (4096 = 12-bit, 65536 = 16-bit). Bindable
    /// so the GUI's resolution toggle can rescale the Y axis live.</summary>
    public static readonly StyledProperty<int> FullScaleProperty =
        AvaloniaProperty.Register<WaveformView, int>(nameof(FullScale), 4096);

    public int FullScale
    {
        get => GetValue(FullScaleProperty);
        set => SetValue(FullScaleProperty, value);
    }

    private const double LeftAxisWidth = 36;
    private const double BottomAxisHeight = 18;
    private const double Padding = 6;

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
    private static readonly IBrush MidRailBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly IPen MidRailPen = new Pen(MidRailBrush, 0.5, new DashStyle(new double[] { 4, 4 }, 0));

    private static readonly Typeface AxisTypeface = new("Menlo,Consolas,monospace", FontStyle.Normal, FontWeight.Normal);

    public static readonly StyledProperty<WaveformModel?> ModelProperty =
        AvaloniaProperty.Register<WaveformView, WaveformModel?>(nameof(Model));

    public WaveformModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private readonly DispatcherTimer _redrawTimer;

    public static readonly StyledProperty<double> YMinProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(YMin), -1.0);

    public double YMin
    {
        get => GetValue(YMinProperty);
        set => SetValue(YMinProperty, value);
    }

    public static readonly StyledProperty<double> YMaxProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(YMax), -1.0);

    public double YMax
    {
        get => GetValue(YMaxProperty);
        set => SetValue(YMaxProperty, value);
    }

    static WaveformView()
    {
        AffectsRender<WaveformView>(FullScaleProperty, YMinProperty, YMaxProperty);
    }

    public WaveformView()
    {
        // 60 fps. Aligns with standard 60 Hz displays; on ProMotion (120 Hz)
        // the compositor will just present every other tick. The per-frame
        // cost is trivial (snapshot a 2048-element ring buffer, draw one
        // StreamGeometry per channel through Skia).
        _redrawTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
        _redrawTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= LeftAxisWidth + Padding || bounds.Height <= BottomAxisHeight + Padding)
        {
            return;
        }

        var plotRect = new Rect(
            LeftAxisWidth,
            Padding,
            bounds.Width - LeftAxisWidth - Padding,
            bounds.Height - BottomAxisHeight - Padding);

        int fullScale = Math.Max(2, FullScale);
        double yMin = YMin, yMax = YMax;
        double displayMin = (yMin >= 0 && yMax > yMin) ? yMin : 0;
        double displayMax = (yMin >= 0 && yMax > yMin) ? yMax : fullScale;

        DrawAxes(context, plotRect, displayMin, displayMax);

        var model = Model;
        if (model is null) return;

        double yRange = Math.Max(1, displayMax - displayMin);
        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            var samples = model.Snapshot(ch);
            if (samples.Length < 2) continue;

            var pen = new Pen(ChannelBrushes[ch % ChannelBrushes.Length], 1.2);
            double xScale = plotRect.Width / Math.Max(1, samples.Length - 1);
            double yScale = plotRect.Height / yRange;

            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                gctx.BeginFigure(new Point(plotRect.Left, plotRect.Bottom - (samples[0] - displayMin) * yScale), false);
                for (int i = 1; i < samples.Length; i++)
                {
                    gctx.LineTo(new Point(plotRect.Left + i * xScale, plotRect.Bottom - (samples[i] - displayMin) * yScale));
                }
                gctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }

        // Footer: x-axis hint
        var xLabel = new FormattedText(
            "samples (rolling window)",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface, 10.5,
            AxisTextBrush);
        context.DrawText(xLabel,
            new Point(plotRect.Left + (plotRect.Width - xLabel.Width) / 2,
                      bounds.Height - BottomAxisHeight + 2));
    }

    private static void DrawAxes(DrawingContext context, Rect plotRect, double displayMin, double displayMax)
    {
        // Mid-rail dashed line at visual centre
        double mid = plotRect.Top + plotRect.Height / 2;
        context.DrawLine(MidRailPen, new Point(plotRect.Left, mid), new Point(plotRect.Right, mid));

        double range = Math.Max(1, displayMax - displayMin);
        double[] vals =
        {
            displayMin,
            displayMin + range * 0.25,
            displayMin + range * 0.50,
            displayMin + range * 0.75,
            displayMax,
        };
        foreach (var val in vals)
        {
            double y = plotRect.Bottom - ((val - displayMin) / range) * plotRect.Height;
            context.DrawLine(GridPen, new Point(plotRect.Left, y), new Point(plotRect.Right, y));

            var label = new FormattedText(
                ((int)Math.Round(val)).ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface, 10,
                AxisTextBrush);
            context.DrawText(label,
                new Point(plotRect.Left - label.Width - 6, y - label.Height / 2));
        }
    }
}
