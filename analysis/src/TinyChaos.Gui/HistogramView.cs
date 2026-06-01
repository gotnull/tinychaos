using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Cumulative per-channel histogram. Renders each channel as a line
/// envelope (filled with low-alpha) over the ADC code range 0..4095.
/// X-axis tick labels at 0, 1024, 2048, 3072, 4095.
/// </summary>
public sealed class HistogramView : Control
{
    private const int Bins = 4096;
    private const double LeftAxisWidth = 36;
    private const double BottomAxisHeight = 18;
    private const double Padding = 6;

    private static readonly Color[] ChannelColors =
    {
        Color.FromRgb(0x4F, 0xC3, 0xF7),
        Color.FromRgb(0xFF, 0x80, 0x40),
        Color.FromRgb(0x81, 0xC7, 0x84),
        Color.FromRgb(0xCE, 0x93, 0xD8),
    };

    private static readonly IBrush AxisTextBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xC0, 0xC8, 0xD8));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GridPen = new Pen(GridBrush, 0.5);

    private static readonly Typeface AxisTypeface = new("Menlo,Consolas,monospace", FontStyle.Normal, FontWeight.Normal);

    public static readonly StyledProperty<HistogramModel?> ModelProperty =
        AvaloniaProperty.Register<HistogramView, HistogramModel?>(nameof(Model));

    public HistogramModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private readonly DispatcherTimer _redrawTimer;

    public HistogramView()
    {
        _redrawTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, OnTick);
        _redrawTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        if (bounds.Width <= LeftAxisWidth + Padding || bounds.Height <= BottomAxisHeight + Padding) return;

        var plotRect = new Rect(
            LeftAxisWidth,
            Padding,
            bounds.Width - LeftAxisWidth - Padding,
            bounds.Height - BottomAxisHeight - Padding);

        var model = Model;
        if (model is null)
        {
            DrawAxes(context, plotRect, Bins);
            return;
        }
        DrawAxes(context, plotRect, model.Bins);

        long max = 1;
        var snapshots = new long[model.ChannelCount][];
        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            snapshots[ch] = model.Snapshot(ch);
            for (int i = 0; i < snapshots[ch].Length; i++)
                if (snapshots[ch][i] > max) max = snapshots[ch][i];
        }

        double xScale = plotRect.Width / Math.Max(1, model.Bins - 1);
        double yScale = plotRect.Height / max;

        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            var counts = snapshots[ch];
            if (counts.Length < 2) continue;

            var color = ChannelColors[ch % ChannelColors.Length];
            var linePen = new Pen(new SolidColorBrush(color), 1.0);
            var fillBrush = new SolidColorBrush(color, 0.18);

            var lineGeom = new StreamGeometry();
            var fillGeom = new StreamGeometry();

            using (var lctx = lineGeom.Open())
            using (var fctx = fillGeom.Open())
            {
                lctx.BeginFigure(new Point(plotRect.Left, plotRect.Bottom - counts[0] * yScale), false);
                fctx.BeginFigure(new Point(plotRect.Left, plotRect.Bottom), true);
                fctx.LineTo(new Point(plotRect.Left, plotRect.Bottom - counts[0] * yScale));

                for (int i = 1; i < counts.Length; i++)
                {
                    double x = plotRect.Left + i * xScale;
                    double y = plotRect.Bottom - counts[i] * yScale;
                    lctx.LineTo(new Point(x, y));
                    fctx.LineTo(new Point(x, y));
                }

                fctx.LineTo(new Point(plotRect.Right, plotRect.Bottom));
                fctx.EndFigure(true);
                lctx.EndFigure(false);
            }

            context.DrawGeometry(fillBrush, null, fillGeom);
            context.DrawGeometry(null, linePen, lineGeom);
        }

        // No in-chart x-axis caption: the card header already shows
        // "ADC code: 0 to 4095 (12-bit)" right next to the title, and the
        // tick labels (0 / 1024 / 2048 / 3072 / 4095) in the bottom margin
        // make the units self-evident. A second caption only collides with
        // the middle tick.
    }

    private static void DrawAxes(DrawingContext context, Rect plotRect, int bins)
    {
        // Ticks at 0 / quarter / half / three-quarter / max, derived from the
        // bin count (12-bit -> 0,1024,2048,3072,4095; 16-bit -> ...,65535).
        int[] codes = { 0, bins / 4, bins / 2, 3 * bins / 4, bins - 1 };
        foreach (var code in codes)
        {
            double x = plotRect.Left + (code / (double)(bins - 1)) * plotRect.Width;
            context.DrawLine(GridPen, new Point(x, plotRect.Top), new Point(x, plotRect.Bottom));

            var label = new FormattedText(
                code.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                AxisTypeface, 10,
                AxisTextBrush);
            // Place label inside the bottom margin, centred on tick
            double tx = code switch
            {
                0 => x + 2,
                4095 => x - label.Width - 2,
                _ => x - label.Width / 2,
            };
            context.DrawText(label, new Point(tx, plotRect.Bottom + 2));
        }

        // y-axis label "count"
        var yLabel = new FormattedText(
            "count",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            AxisTypeface, 10,
            AxisTextBrush);
        context.DrawText(yLabel, new Point(plotRect.Left - yLabel.Width - 6, plotRect.Top));
    }
}
