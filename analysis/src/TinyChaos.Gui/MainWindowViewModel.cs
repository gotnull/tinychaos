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
    private readonly BuildFlashService _buildFlash = new();
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

    /// <summary>
    /// Bound to the Samples ListBox <c>SelectedItems</c>. Avalonia keeps this
    /// in sync as the user clicks / Ctrl-clicks / Shift-clicks rows.
    /// </summary>
    public ObservableCollection<SampleEntry> SelectedSamples { get; } = new();

    [ObservableProperty]
    private string _samplesDirectory;

    [ObservableProperty]
    private SampleEntry? _selectedSample;

    [ObservableProperty]
    private string _samplesStatusText = "";

    // Saved-data viewer (Samples tab): the full decoded capture currently shown,
    // plus a status line. Loaded on selection from the samples list.
    [ObservableProperty]
    private CaptureBuffer? _loadedCapture;

    [ObservableProperty]
    private string _viewerStatusText = "select a capture to view";

    /// <summary>
    /// Decode a sample file in full and hand it to the saved-data viewer. Called
    /// when the user selects a row in the samples list (single click). Decoding
    /// is CRC-validated via the framer; a corrupt/empty file yields a message
    /// rather than throwing.
    /// </summary>
    public void ShowSampleInViewer(SampleEntry? entry)
    {
        if (entry is null) return;
        try
        {
            var buf = CaptureBuffer.Load(entry.FullPath, channelCount: 2);
            LoadedCapture = buf;
            ViewerStatusText = buf.Length > 0
                ? $"{entry.FileName} — {buf.PacketCount:N0} packets, {buf.Length:N0} samples/ch"
                : $"{entry.FileName} — no valid packets";
        }
        catch (Exception ex)
        {
            LoadedCapture = null;
            ViewerStatusText = $"load failed: {ex.Message}";
        }
    }

    [ObservableProperty]
    private int _activeTabIndex;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string _deleteConfirmText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeletableSelection))]
    private int _deletableSelectedCount;

    public bool HasDeletableSelection => DeletableSelectedCount > 0;

    // Status footer pills.
    [ObservableProperty] private string _packetsText = "0";
    [ObservableProperty] private string _badCrcText = "0";
    [ObservableProperty] private string _dropsText = "0";
    [ObservableProperty] private string _resyncText = "0 B";
    [ObservableProperty] private string _stm32RateText = "0.0 Hz";
    [ObservableProperty] private string _hostRateText = "0.0 Hz";
    [ObservableProperty] private string _validationLabelEcho = "none";
    [ObservableProperty] private string _modeText = "live";

    // Build / Flash card
    [ObservableProperty] private string _firmwareDirectory;
    [ObservableProperty] private string _buildLog = "";
    [ObservableProperty] private string _buildStatusText = "idle";
    [ObservableProperty] private IBrush _buildStatusDotBrush = StatusBrushes.Idle;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFlashSelectedBin))]
    private bool _isBusyBuilding;

    // A prebuilt firmware .bin (e.g. downloaded from GitHub Releases) the user
    // picked via Browse; flashed with st-flash, no build/toolchain required.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFlashSelectedBin))]
    private string _selectedFirmwareBin = "";

    public bool CanFlashSelectedBin =>
        !IsBusyBuilding && !string.IsNullOrWhiteSpace(SelectedFirmwareBin) && File.Exists(SelectedFirmwareBin);

    // Recording (live capture only)
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _recordButtonLabel = "Record";
    [ObservableProperty] private string _recordingPathText = "";

    // Entropy harvest (live capture only)
    [ObservableProperty] private bool _isHarvesting;
    [ObservableProperty] private string _harvestButtonLabel = "Harvest";
    [ObservableProperty] private string _harvestStatusText = "";

    // Live zener spike-activity readout (avalanche-quality metric, updated 10 Hz).
    [ObservableProperty] private string _spikeRateText = "waiting for data…";

    public WaveformModel Waveform { get; }
    public HistogramModel Histogram { get; }

    // ADC sample resolution. The firmware streams raw ADC codes; this tells the
    // GUI how to scale the waveform Y-axis and how many histogram bins to use.
    // 12-bit (0..4095) is the project default; 16-bit (0..65535) is for boards
    // configured at full H7 ADC resolution. Switching rebins the histogram in
    // place (same model instance) and rescales the waveform live.
    public int[] ResolutionOptions { get; } = { 12, 16 };

    [ObservableProperty] private int _resolutionBits = 12;

    /// <summary>Full-scale code count for the waveform Y-axis (1 &lt;&lt; bits).</summary>
    public int WaveformFullScale => 1 << ResolutionBits;

    /// <summary>Caption shown by the DISTRIBUTION card header.</summary>
    public string AdcRangeCaption => $"ADC code: 0 to {(1 << ResolutionBits) - 1} ({ResolutionBits}-bit)";

    partial void OnResolutionBitsChanged(int value)
    {
        Histogram.Reconfigure(value);
        OnPropertyChanged(nameof(WaveformFullScale));
        OnPropertyChanged(nameof(AdcRangeCaption));
    }

    /// <summary>Y-axis lower bound for the waveform zoom view. -1 = full-scale auto.</summary>
    [ObservableProperty] private double _waveformYMin = -1.0;

    /// <summary>Y-axis upper bound for the waveform zoom view. -1 = full-scale auto.</summary>
    [ObservableProperty] private double _waveformYMax = -1.0;

    /// <summary>When true, Y range continuously refits channel 0 on every UI tick.</summary>
    [ObservableProperty] private bool _autoZoomEnabled = false;

    [RelayCommand]
    private void LockRange()
    {
        AutoZoomEnabled = false;
        var (min, max) = Waveform.GetMinMax(0);
        if (min >= max) return;
        double margin = (max - min) * 0.10;
        WaveformYMin = Math.Max(0, min - margin);
        WaveformYMax = Math.Min(WaveformFullScale, max + margin);
    }

    [RelayCommand]
    private void ResetRange()
    {
        AutoZoomEnabled = false;
        WaveformYMin = -1.0;
        WaveformYMax = -1.0;
    }

    public MainWindowViewModel()
    {
        Waveform = new WaveformModel(channelCount: 2, windowSamples: 2048);
        Histogram = new HistogramModel(channelCount: 2, bits: 12);
        _capture = new CaptureService(Waveform, Histogram, channelCount: 2);

        _samplesDirectory = DefaultSamplesDirectory;
        EnsureDirectoryExists(_samplesDirectory);
        _firmwareDirectory = BuildFlashService.FindFirmwareDirectory() ?? "(firmware directory not found)";

        // Recompute the deletable-selected count whenever the selection changes.
        SelectedSamples.CollectionChanged += (_, _) => RecomputeDeletableCount();

        RefreshPorts();
        RefreshSamples();

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnUiTick);
        _uiTimer.Start();
    }

    private void RecomputeDeletableCount()
    {
        int n = 0;
        foreach (var s in SelectedSamples)
        {
            if (!s.IsDemo) n++;
        }
        DeletableSelectedCount = n;
    }

    partial void OnValidationLabelChanged(string value)
    {
        ValidationLabelEcho = string.IsNullOrWhiteSpace(value) ? "none" : value;
    }

    /// <summary>
    /// Replay the given sample and switch to the Live capture tab so the
    /// user sees the result immediately. Called from the ListBox
    /// DoubleTapped handler in the code-behind. Single-clicking a sample no
    /// longer auto-plays; that change makes multi-select practical (Ctrl /
    /// Shift clicks build up a selection without firing replays).
    /// </summary>
    public void PlaySample(SampleEntry entry)
    {
        try
        {
            _capture.Reset();
            ModeText = "replay";
            ConnectionStatusText = $"Replaying {entry.FileName}";
            StatusDotBrush = StatusBrushes.Warning;
            ConnectButtonLabel = "Connect";
            _ = _capture.ReplayFileAsync(entry.FullPath);
            ActiveTabIndex = 0;
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
            // SerialPort.GetPortNames() on macOS returns both /dev/tty.* (incoming)
            // and /dev/cu.* (callout) for every hardware port, plus Bluetooth-Incoming-Port
            // and debug-console pseudo-ports we never want. Linux mostly returns clean
            // /dev/ttyACMx / /dev/ttyUSBx. Windows returns COMx. Whitelist the
            // useful prefixes and drop the rest so the dropdown only shows real ports.
            var names = SerialPort.GetPortNames();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names)
            {
                if (!IsLikelyUsbSerialPort(raw)) continue;
                if (!seen.Add(raw)) continue;
                AvailablePorts.Add(raw);
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

    /// <summary>Keep only candidate USB serial / virtual COM ports; drop tty.* duplicates,
    /// Bluetooth pseudo-ports, and macOS debug-console.</summary>
    private static bool IsLikelyUsbSerialPort(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // macOS: prefer /dev/cu.* over /dev/tty.*; drop pseudo-ports.
        if (name.StartsWith("/dev/tty.", StringComparison.Ordinal)) return false;
        if (name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("debug-console", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("/dev/cu.usbmodem", StringComparison.Ordinal) ||
            name.StartsWith("/dev/cu.usbserial", StringComparison.Ordinal) ||
            name.StartsWith("/dev/cu.SLAB_", StringComparison.Ordinal) ||
            name.StartsWith("/dev/cu.wchusb", StringComparison.Ordinal))
        {
            return true;
        }

        // Linux: typical USB serial endpoints.
        if (name.StartsWith("/dev/ttyACM", StringComparison.Ordinal) ||
            name.StartsWith("/dev/ttyUSB", StringComparison.Ordinal))
        {
            return true;
        }

        // Windows: COMx.
        if (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
            name.Length > 3 && char.IsDigit(name[3]))
        {
            return true;
        }

        return false;
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
    private void RequestDeleteSamples()
    {
        // Filter out demo samples; they are protected by IsDemo.
        int n = DeletableSelectedCount;
        if (n == 0)
        {
            // Nothing deletable selected.
            return;
        }
        DeleteConfirmText = n == 1
            ? "Delete the selected sample? This cannot be undone."
            : $"Delete {n} selected samples? This cannot be undone.";
        IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void ConfirmDeleteSamples()
    {
        // Snapshot the to-delete list first; SelectedSamples may shrink as
        // each file disappears from the underlying Samples collection.
        var toDelete = new List<SampleEntry>();
        foreach (var s in SelectedSamples)
        {
            if (!s.IsDemo) toDelete.Add(s);
        }
        int ok = 0, failed = 0;
        foreach (var s in toDelete)
        {
            try
            {
                File.Delete(s.FullPath);
                ok++;
            }
            catch
            {
                failed++;
            }
        }
        IsConfirmingDelete = false;
        DeleteConfirmText = "";
        RefreshSamples();
        SamplesStatusText = failed == 0
            ? $"deleted {ok} sample{(ok == 1 ? "" : "s")}"
            : $"deleted {ok}, failed to delete {failed}";
    }

    [RelayCommand]
    private void CancelDeleteSamples()
    {
        IsConfirmingDelete = false;
        DeleteConfirmText = "";
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
    private void ToggleRecording()
    {
        if (_capture.IsRecording)
        {
            _capture.StopRecording();
            IsRecording = false;
            RecordButtonLabel = "Record";
            RecordingPathText = "";
            // The new file is now flushed and visible in the samples folder.
            RefreshSamples();
            return;
        }
        if (!_capture.IsRunning)
        {
            ConnectionStatusText = "Connect a live capture before recording";
            StatusDotBrush = StatusBrushes.Warning;
            return;
        }
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var labelToken = string.IsNullOrWhiteSpace(ValidationLabel) ? "capture" : ValidationLabel;
        var safeLabel = string.Concat(labelToken.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(SamplesDirectory, $"{safeLabel}-{ts}.bin");
        try
        {
            _capture.StartRecording(path);
            IsRecording = true;
            RecordButtonLabel = "Stop";
            RecordingPathText = path;
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Recording failed: {ex.Message}";
            StatusDotBrush = StatusBrushes.Error;
        }
    }

    [RelayCommand]
    private void ToggleHarvesting()
    {
        if (_capture.IsHarvesting)
        {
            _capture.StopHarvesting();
            IsHarvesting = false;
            HarvestButtonLabel = "Harvest";
            HarvestStatusText = "";
            return;
        }
        if (!_capture.IsRunning)
        {
            ConnectionStatusText = "Connect a live capture before harvesting";
            StatusDotBrush = StatusBrushes.Warning;
            return;
        }
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var labelToken = string.IsNullOrWhiteSpace(ValidationLabel) ? "harvest" : ValidationLabel;
        var safeLabel = string.Concat(labelToken.Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(SamplesDirectory, $"{safeLabel}-{ts}.hex");
        try
        {
            _capture.StartHarvesting(path);
            IsHarvesting = true;
            HarvestButtonLabel = "Stop harvest";
            HarvestStatusText = path;
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Harvest failed: {ex.Message}";
            StatusDotBrush = StatusBrushes.Error;
        }
    }

    [RelayCommand]
    private void ToggleConnection()
    {
        if (_capture.IsRunning)
        {
            _capture.Stop();
            // Stopping the capture also stops recording and harvest.
            // Mirror that into the view-model state.
            if (IsRecording)
            {
                IsRecording = false;
                RecordButtonLabel = "Record";
                RecordingPathText = "";
                RefreshSamples();
            }
            if (IsHarvesting)
            {
                IsHarvesting = false;
                HarvestButtonLabel = "Harvest";
                HarvestStatusText = "";
            }
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

        // Spike activity on the zener channel: up/down crossings of the rolling
        // noise baseline over the last N samples, plus a density % - the live
        // "how much avalanche is my circuit producing" number.
        var sr = s.SpikeRate;
        if (sr.Filled == 0)
        {
            SpikeRateText = "waiting for data…";
        }
        else
        {
            SpikeRateText =
                $"▲ {sr.Up,7:N0} up    ▼ {sr.Down,7:N0} down    " +
                $"{sr.Percent,6:F2}% active    " +
                $"({sr.Total:N0} spikes in last {sr.Filled:N0} samples)";
        }

        PacketsText = s.Packets.ToString("N0");
        BadCrcText = s.BadCrc.ToString("N0");
        DropsText = s.Drops.ToString("N0");
        ResyncText = s.ResyncBytes >= 1024 ? $"{s.ResyncBytes / 1024.0:F1} KB" : $"{s.ResyncBytes} B";
        Stm32RateText = $"{s.Stm32RateHz:F1} Hz";
        HostRateText = $"{s.HostRateHz:F1} Hz";

        // Device-side screen tap: jump to the Live Capture tab so the user
        // sees the waveform immediately. The flag is latched per packet on
        // the read thread and cleared by Snapshot(), so this fires once per
        // tap rather than every tick.
        if (s.DeviceTapped)
        {
            ActiveTabIndex = 0;
        }

        if (IsHarvesting)
        {
            if (!_capture.IsHarvesting)
            {
                // Harvest stopped unexpectedly (e.g. connection dropped mid-harvest).
                IsHarvesting = false;
                HarvestButtonLabel = "Harvest";
                HarvestStatusText = "";
            }
            else
            {
                long bytes = _capture.HarvestBytesWritten;
                HarvestStatusText = $"{_capture.HarvestPath}  ({bytes:N0} bytes)";
            }
        }

        if (AutoZoomEnabled)
        {
            var (min, max) = Waveform.GetMinMax(0);
            if (max > min)
            {
                double margin = (max - min) * 0.10;
                WaveformYMin = Math.Max(0, min - margin);
                WaveformYMax = Math.Min(WaveformFullScale, max + margin);
            }
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* swallow */ }
    }

    // ----- Build & Flash commands -----

    // Build + flash drive the committed nucleo-h753zi CMake project via its
    // flash.sh / flash.ps1 wrapper. The self-test stays on `make test` (the
    // portable protocol check, no STM32 toolchain needed).
    [RelayCommand]
    private Task BuildFirmware() => RunFirmwareJobAsync("build",
        onLine => _buildFlash.RunFlashScriptAsync(FirmwareDirectory, "build", onLine));

    [RelayCommand]
    private Task FlashFirmware() => RunFirmwareJobAsync("flash",
        onLine => _buildFlash.RunFlashScriptAsync(FirmwareDirectory, "flash", onLine));

    [RelayCommand]
    private Task RunFirmwareSelfTest() => RunFirmwareJobAsync("host test",
        onLine => _buildFlash.RunMakeAsync(FirmwareDirectory, "test", onLine));

    // Flash a prebuilt .bin the user browsed to (no build, only st-flash needed).
    [RelayCommand]
    private Task FlashSelectedBin() => RunFirmwareJobAsync("flash .bin",
        onLine => _buildFlash.RunStFlashAsync(SelectedFirmwareBin, onLine));

    [RelayCommand]
    private void ClearBuildLog() => BuildLog = "";

    private async Task RunFirmwareJobAsync(string label, Func<Action<string>, Task<int>> job)
    {
        if (IsBusyBuilding)
        {
            AppendBuildLine("! a build/flash is already running");
            return;
        }
        IsBusyBuilding = true;
        BuildStatusText = $"{label}ing...";
        BuildStatusDotBrush = StatusBrushes.Warning;
        try
        {
            int code = await job(AppendBuildLine);
            if (code == 0)
            {
                BuildStatusText = $"{label} ok";
                BuildStatusDotBrush = StatusBrushes.Connected;
            }
            else
            {
                BuildStatusText = $"{label} failed (exit {code})";
                BuildStatusDotBrush = StatusBrushes.Error;
            }
        }
        catch (Exception ex)
        {
            AppendBuildLine($"! exception: {ex.Message}");
            BuildStatusText = $"{label} crashed";
            BuildStatusDotBrush = StatusBrushes.Error;
        }
        finally
        {
            IsBusyBuilding = false;
        }
    }

    private void AppendBuildLine(string line)
    {
        // Marshal back to the UI thread; the subprocess streams output from
        // a worker thread.
        Dispatcher.UIThread.Post(() =>
        {
            BuildLog = string.IsNullOrEmpty(BuildLog) ? line : BuildLog + "\n" + line;
        });
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
