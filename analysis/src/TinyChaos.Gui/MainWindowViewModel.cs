using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TinyChaos.Gui;

/// <summary>
/// Main view model. Owns the capture service, the waveform and histogram
/// models, and the stats text. Refreshes UI strings on a 10 Hz dispatcher
/// timer; the canvas views poll their models on their own redraw timer.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly CaptureService _capture;
    private readonly DispatcherTimer _uiTimer;

    public ObservableCollection<string> AvailablePorts { get; } = new();

    [ObservableProperty]
    private string? _selectedPort;

    [ObservableProperty]
    private string _validationLabel = "";

    [ObservableProperty]
    private string _connectButtonLabel = "Connect";

    [ObservableProperty]
    private string _statsText = "no samples";

    [ObservableProperty]
    private string _statusLine = "idle";

    public WaveformModel Waveform { get; }
    public HistogramModel Histogram { get; }

    public MainWindowViewModel()
    {
        Waveform = new WaveformModel(channelCount: 2, windowSamples: 2048);
        Histogram = new HistogramModel(channelCount: 2, bits: 12);
        _capture = new CaptureService(Waveform, Histogram, channelCount: 2);

        RefreshPorts();

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnUiTick);
        _uiTimer.Start();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        try
        {
            foreach (var name in SerialPort.GetPortNames())
            {
                AvailablePorts.Add(name);
            }
        }
        catch (Exception ex)
        {
            StatusLine = $"port enumeration failed: {ex.Message}";
        }
        if (SelectedPort is null && AvailablePorts.Count > 0)
        {
            SelectedPort = AvailablePorts[0];
        }
    }

    [RelayCommand]
    private void ToggleConnection()
    {
        if (_capture.IsRunning)
        {
            _capture.Stop();
            ConnectButtonLabel = "Connect";
            StatusLine = "disconnected";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            StatusLine = "select a port first";
            return;
        }
        try
        {
            _capture.Start(SelectedPort!, baudRate: 921600);
            ConnectButtonLabel = "Disconnect";
            StatusLine = $"connected to {SelectedPort}";
        }
        catch (Exception ex)
        {
            StatusLine = $"open failed: {ex.Message}";
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        // Build stats and status strings from the latest counters. The canvases
        // poll their own models so we do not touch them here.
        var stats = _capture.Snapshot();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Per-channel statistics");
        for (int ch = 0; ch < stats.ChannelStats.Length; ch++)
        {
            var s = stats.ChannelStats[ch];
            if (s.Count == 0)
            {
                sb.AppendLine($"  channel {ch}: no samples");
            }
            else
            {
                sb.AppendLine($"  channel {ch}: n={s.Count} min={s.Min:F1} max={s.Max:F1} mean={s.Mean:F2} std={s.Std:F3}");
            }
        }
        StatsText = sb.ToString().TrimEnd();

        StatusLine =
            $"pkts={stats.Packets,6:D}  bad_crc={stats.BadCrc,3:D}  drops={stats.Drops,4:D}  " +
            $"resync={stats.ResyncBytes,5:D}B  stm32={stats.Stm32RateHz,7:F1}Hz  host={stats.HostRateHz,7:F1}Hz  " +
            $"label={ValidationLabel}";
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _capture.Stop();
        _capture.Dispose();
    }
}
