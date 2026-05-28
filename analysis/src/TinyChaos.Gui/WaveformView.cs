using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Custom canvas that draws a rolling-window waveform for every channel of
/// a <see cref="WaveformModel"/>. Polls the model on a dispatcher timer at
/// 30 Hz; no per-sample UI churn.
/// </summary>
public sealed class WaveformView : Control
{
    private static readonly IBrush[] ChannelBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)), // channel 0 light blue
        new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x40)), // channel 1 orange
        new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84)), // channel 2 green
        new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8)), // channel 3 violet
    };

    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
    private static readonly IPen GridPen = new Pen(GridBrush, 0.5);

    public static readonly StyledProperty<WaveformModel?> ModelProperty =
        AvaloniaProperty.Register<WaveformView, WaveformModel?>(nameof(Model));

    public WaveformModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    private readonly DispatcherTimer _redrawTimer;

    public WaveformView()
    {
        _redrawTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick);
        _redrawTimer.Start();
    }

    private void OnTick(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = Bounds;
        // Background is provided by the surrounding Border; just draw grid + traces.

        // Horizontal grid at 1/4 marks (representing 25/50/75% of full scale).
        for (int i = 1; i < 4; i++)
        {
            double y = bounds.Height * i / 4.0;
            context.DrawLine(GridPen, new Point(0, y), new Point(bounds.Width, y));
        }

        var model = Model;
        if (model is null) return;

        for (int ch = 0; ch < model.ChannelCount; ch++)
        {
            var samples = model.Snapshot(ch);
            if (samples.Length < 2) continue;

            var pen = new Pen(ChannelBrushes[ch % ChannelBrushes.Length], 1.0);
            double xScale = bounds.Width / Math.Max(1, samples.Length - 1);
            double yScale = bounds.Height / 4096.0; // 12-bit full scale

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(0, bounds.Height - samples[0] * yScale), false);
                for (int i = 1; i < samples.Length; i++)
                {
                    ctx.LineTo(new Point(i * xScale, bounds.Height - samples[i] * yScale));
                }
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
