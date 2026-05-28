using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TinyChaos.Gui;

/// <summary>
/// Main view model. Owns the capture service, the waveform and histogram
/// models, and all the per-pill status strings. Refreshes UI strings on a
/// 10 Hz dispatcher timer; the canvas views poll their models on their own
/// redraw timer.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly string DefaultSamplesDirectory = ResolveDefaultSamplesDirectory();

    /// <summary>
    /// Find the canonical samples directory. Priority:
    /// 1. The TINYCHAOS_SAMPLES environment variable, if it points at an
    ///    existing directory.
    /// 2. Walk up from the executable directory looking for a folder named
    ///    <c>samples</c> sitting alongside a <c>.git</c> entry. This finds
    ///    the in-repo samples folder during development and in checkouts.
    /// 3. Fall back to <c>~/tinychaos-samples</c>, which the GUI will
    ///    create on first run.
    /// </summary>
    private static string ResolveDefaultSamplesDirectory()
    {
        var env = Environment.GetEnvironmentVariable("TINYCHAOS_SAMPLES");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var samples = Path.Combine(dir, "samples");
            var gitEntry = Path.Combine(dir, ".git");
            if (Directory.Exists(samples) && (Directory.Exists(gitEntry) || File.Exists(gitEntry)))
            {
                return samples;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "tinychaos-samples");
    }

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
    private string _connectionStatusText = "Disconnected";

    [ObservableProperty]
    private IBrush _statusDotBrush = StatusBrushes.Idle;

    [ObservableProperty]
    private string _statsText = "  channel 0: no samples yet\n  channel 1: no samples yet";

    // Samples section
    public ObservableCollection<SampleEntry> Samples { get; } = new();

    [ObservableProperty]
    private string _samplesDirectory;

    [ObservableProperty]
    private SampleEntry? _selectedSample;

    [ObservableProperty]
    private string _samplesStatusText = "";

    // Status footer pills.
    [ObservableProperty] private string _packetsText = "0";
    [ObservableProperty] private string _badCrcText = "0";
    [ObservableProperty] private string _dropsText = "0";
    [ObservableProperty] private string _resyncText = "0 B";
    [ObservableProperty] private string _stm32RateText = "0.0 Hz";
    [ObservableProperty] private string _hostRateText = "0.0 Hz";
    [ObservableProperty] private string _validationLabelEcho = "none";
    [ObservableProperty] private string _modeText = "live";

    public WaveformModel Waveform { get; }
    public HistogramModel Histogram { get; }

    public MainWindowViewModel()
    {
        Waveform = new WaveformModel(channelCount: 2, windowSamples: 2048);
        Histogram = new HistogramModel(channelCount: 2, bits: 12);
        _capture = new CaptureService(Waveform, Histogram, channelCount: 2);

        _samplesDirectory = DefaultSamplesDirectory;
        EnsureDirectoryExists(_samplesDirectory);

        RefreshPorts();
        RefreshSamples();

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnUiTick);
        _uiTimer.Start();
    }

    partial void OnValidationLabelChanged(string value)
    {
        ValidationLabelEcho = string.IsNullOrWhiteSpace(value) ? "none" : value;
    }

    partial void OnSelectedSampleChanged(SampleEntry? value)
    {
        if (value is null) return;
        try
        {
            _capture.Reset();
            ModeText = "replay";
            ConnectionStatusText = $"Replaying {value.FileName}";
            StatusDotBrush = StatusBrushes.Warning;
            ConnectButtonLabel = "Connect";
            _ = _capture.ReplayFileAsync(value.FullPath);
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Replay failed: {ex.Message}";
            StatusDotBrush = StatusBrushes.Error;
        }
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
            ConnectionStatusText = $"port enumeration failed: {ex.Message}";
            StatusDotBrush = StatusBrushes.Error;
        }
        if (SelectedPort is null && AvailablePorts.Count > 0)
        {
            SelectedPort = AvailablePorts[0];
        }
    }

    [RelayCommand]
    private void RefreshSamples()
    {
        Samples.Clear();
        try
        {
            EnsureDirectoryExists(SamplesDirectory);
            var dir = new DirectoryInfo(SamplesDirectory);
            var files = dir.EnumerateFiles("*.bin", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var f in files)
            {
                Samples.Add(new SampleEntry(f));
            }
            SamplesStatusText = Samples.Count switch
            {
                0 => $"no .bin files in {SamplesDirectory}",
                1 => "1 sample",
                _ => $"{Samples.Count} samples",
            };
        }
        catch (Exception ex)
        {
            SamplesStatusText = $"error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenSamplesFolder()
    {
        try
        {
            EnsureDirectoryExists(SamplesDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "explorer" :
                           OperatingSystem.IsMacOS() ? "open" :
                           "xdg-open",
                Arguments = $"\"{SamplesDirectory}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            SamplesStatusText = $"could not open folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleConnection()
    {
        if (_capture.IsRunning)
        {
            _capture.Stop();
            ConnectButtonLabel = "Connect";
            ConnectionStatusText = "Disconnected";
            StatusDotBrush = StatusBrushes.Idle;
            ModeText = "live";
            return;
        }
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            ConnectionStatusText = "Select a port first";
            StatusDotBrush = StatusBrushes.Warning;
            return;
        }
        try
        {
            _capture.Reset();
            SelectedSample = null;
            _capture.Start(SelectedPort!, baudRate: 921600);
            ConnectButtonLabel = "Disconnect";
            ConnectionStatusText = $"Connected to {SelectedPort}";
            StatusDotBrush = StatusBrushes.Connected;
            ModeText = "live";
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Open failed: {ex.Message}";
            StatusDotBrush = StatusBrushes.Error;
        }
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        var s = _capture.Snapshot();

        var sb = new System.Text.StringBuilder();
        for (int ch = 0; ch < s.ChannelStats.Length; ch++)
        {
            var c = s.ChannelStats[ch];
            if (c.Count == 0)
            {
                sb.AppendLine($"  channel {ch}    no samples yet");
            }
            else
            {
                sb.AppendLine(
                    $"  channel {ch}    n = {c.Count,10:N0}    " +
                    $"min = {c.Min,7:F1}    max = {c.Max,7:F1}    " +
                    $"mean = {c.Mean,8:F2}    std = {c.Std,8:F3}");
            }
        }
        StatsText = sb.ToString().TrimEnd();

        PacketsText = s.Packets.ToString("N0");
        BadCrcText = s.BadCrc.ToString("N0");
        DropsText = s.Drops.ToString("N0");
        ResyncText = s.ResyncBytes >= 1024 ? $"{s.ResyncBytes / 1024.0:F1} KB" : $"{s.ResyncBytes} B";
        Stm32RateText = $"{s.Stm32RateHz:F1} Hz";
        HostRateText = $"{s.HostRateHz:F1} Hz";
    }

    private static void EnsureDirectoryExists(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* swallow */ }
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _capture.Stop();
        _capture.Dispose();
    }
}

internal static class StatusBrushes
{
    public static readonly IBrush Idle      = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x73));
    public static readonly IBrush Connected = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    public static readonly IBrush Warning   = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
    public static readonly IBrush Error     = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
}
