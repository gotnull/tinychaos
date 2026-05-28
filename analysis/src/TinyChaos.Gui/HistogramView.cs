using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Renders per-channel cumulative histograms as overlapping line plots.
/// Bars would obscure each other across channels; we draw counts as line
/// envelopes instead.
/// </summary>
public sealed class HistogramView : Control
{
    private static readonly IBrush[] ChannelBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x40)),
        new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)),
        new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)),
    };

    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GridPen = new Pen(GridBrush, 0.5);

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
        var model = Model;
        if (model is null) return;

        // Vertical grid at quartile codes.
        for (int i = 1; i < 4; i++)
        {
            double x = bounds.Width * i / 4.0;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        // Pre-compute the global max count across channels so the y axis is
        // shared. Without this the channels would not be visually comparable.
        long max = 1;
        var snapshots = new long[model.ChannelCount][];
        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            snapshots[ch] = model.Snapshot(ch);
            for (int i = 0; i < snapshots[ch].Length; i++)
            {
                if (snapshots[ch][i] > max) max = snapshots[ch][i];
            }
        }

        double xScale = bounds.Width / Math.Max(1, model.Bins - 1);
        double yScale = bounds.Height / max;

        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            var counts = snapshots[ch];
            if (counts.Length < 2) continue;

            var pen = new Pen(ChannelBrushes[ch % ChannelBrushes.Length], 1.0);
            var geometry = new StreamGeometry();
            using (var gctx = geometry.Open())
            {
                gctx.BeginFigure(new Point(0, bounds.Height - counts[0] * yScale), false);
                for (int i = 1; i < counts.Length; i++)
                {
                    gctx.LineTo(new Point(i * xScale, bounds.Height - counts[i] * yScale));
                }
                gctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
