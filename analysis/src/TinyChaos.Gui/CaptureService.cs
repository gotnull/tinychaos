using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using TinyChaos.Host.Stats;
using TinyChaos.Protocol;

namespace TinyChaos.Gui;

/// <summary>
/// Snapshot of capture counters at one point in time, taken under a lock so
/// the UI sees a consistent view of every counter.
/// </summary>
public sealed record CaptureSnapshot(
    long Packets,
    long BadCrc,
    long BadVersion,
    long Drops,
    long ResyncBytes,
    long Samples,
    double Stm32RateHz,
    double HostRateHz,
    RollingStatsSnapshot[] ChannelStats,
    // True if at least one packet with the device-tap flag (FLAGS bit 0)
    // arrived since the previous Snapshot(). Latched on the read thread and
    // cleared on read so the UI sees each tap exactly once.
    bool DeviceTapped);

public sealed record RollingStatsSnapshot(long Count, double Mean, double Std, double Min, double Max);

/// <summary>
/// Background reader. Owns the serial port and the framer; pushes samples
/// into the supplied waveform and histogram models. UI consumers poll
/// <see cref="Snapshot"/> on a timer to refresh stats and status strings.
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly WaveformModel _waveform;
    private readonly HistogramModel _histogram;
    private readonly int _channelCount;

    private readonly object _lock = new();
    private long _packets, _badCrc, _badVersion, _drops, _resyncBytes, _samples;
    // Latched when a packet carries the device-tap flag (FLAGS bit 0); read
    // and cleared by Snapshot() so the GUI can react to each tap once.
    private bool _tapPending;
    private DropTracker _dropTracker = new();
    private RateEstimator _rate = new();
    private ChannelStats _channelStats;

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Stopwatch? _wall;

    // Optional raw-stream recording. While non-null, every chunk read from
    // the serial port is also appended to this file before being fed to the
    // framer. Writes happen on the read thread; the GUI manages start/stop.
    private FileStream? _recordingStream;

    // Optional entropy harvest. When active, channel-0 (zener) samples are
    // fed to the model which detects spikes and writes hex bytes to a file.
    private EntropyHarvestModel? _harvest;

    public bool IsRunning { get; private set; }
    public bool IsRecording => _recordingStream != null;
    public string? RecordingPath { get; private set; }
    public bool IsHarvesting => _harvest?.IsOpen ?? false;
    public string? HarvestPath { get; private set; }
    public long HarvestBytesWritten => _harvest?.BytesWritten ?? 0;

    public CaptureService(WaveformModel waveform, HistogramModel histogram, int channelCount)
    {
        _waveform = waveform;
        _histogram = histogram;
        _channelCount = channelCount;
        _channelStats = new ChannelStats(channelCount);
    }

    public void Start(string portName, int baudRate)
    {
        if (IsRunning) return;
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 500,
            DtrEnable = true,
            RtsEnable = true,
        };
        _port.Open();
        _cts = new CancellationTokenSource();
        _wall = Stopwatch.StartNew();
        var token = _cts.Token;
        _readTask = Task.Run(() => ReadLoop(token), token);
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* swallow */ }
        try { _readTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* swallow */ }
        try { _port?.Close(); } catch { /* swallow */ }
        _port?.Dispose();
        _port = null;
        _cts?.Dispose();
        _cts = null;
        _readTask = null;
        StopRecording();
        StopHarvesting();
    }

    /// <summary>
    /// Start writing the raw incoming byte stream to <paramref name="path"/>.
    /// Overwrites any existing file. Recording continues until Stop or
    /// StopRecording is called.
    /// </summary>
    public void StartRecording(string path)
    {
        StopRecording();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _recordingStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        RecordingPath = path;
    }

    /// <summary>Stop an active recording and close the file. No-op if none.</summary>
    public void StopRecording()
    {
        var stream = _recordingStream;
        _recordingStream = null;
        if (stream is null) return;
        try { stream.Flush(); } catch { /* swallow */ }
        try { stream.Dispose(); } catch { /* swallow */ }
        RecordingPath = null;
    }

    /// <summary>
    /// Start detecting avalanche spikes and writing hex bytes to
    /// <paramref name="path"/>. Opens the harvest model; any previously active
    /// harvest is stopped first.
    /// </summary>
    public void StartHarvesting(string path)
    {
        StopHarvesting();
        var h = new EntropyHarvestModel();
        h.Open(path);
        lock (_lock) { _harvest = h; }
        HarvestPath = path;
    }

    /// <summary>Stop an active entropy harvest and close the file. No-op if none.</summary>
    public void StopHarvesting()
    {
        EntropyHarvestModel? h;
        lock (_lock) { h = _harvest; _harvest = null; }
        HarvestPath = null;
        if (h is null) return;
        try { h.Close(); } catch { /* swallow */ }
        try { h.Dispose(); } catch { /* swallow */ }
    }

    /// <summary>
    /// Reset all stats counters and clear the waveform / histogram models.
    /// Called before replaying a file so previous data does not pollute the
    /// view.
    /// </summary>
    public void Reset()
    {
        Stop();
        lock (_lock)
        {
            _packets = _badCrc = _badVersion = _drops = _resyncBytes = _samples = 0;
            _tapPending = false;
            _dropTracker = new DropTracker();
            _rate = new RateEstimator();
            _channelStats = new ChannelStats(_channelCount);
        }
        _waveform.Clear();
        _histogram.Clear();
    }

    /// <summary>
    /// Replay a previously captured raw binary file through the framer.
    /// Stops any active serial capture first. Runs the read on a worker
    /// thread; the UI continues to refresh on its own timer.
    /// </summary>
    public Task ReplayFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"sample file not found: {path}", path);

        Stop();
        _wall = Stopwatch.StartNew();
        IsRunning = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cts = cts;
        var task = Task.Run(() =>
        {
            try
            {
                var framer = new Framer();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var buf = new byte[4096];
                while (!cts.IsCancellationRequested)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    foreach (var ev in framer.Feed(buf.AsMemory(0, n)))
                    {
                        HandleEvent(ev);
                    }
                }
                var trailing = framer.FlushResync();
                if (trailing is not null) HandleEvent(trailing);
            }
            finally
            {
                IsRunning = false;
            }
        }, cts.Token);
        _readTask = task;
        return task;
    }

    public CaptureSnapshot Snapshot()
    {
        lock (_lock)
        {
            var perChannel = new RollingStatsSnapshot[_channelCount];
            for (int ch = 0; ch < _channelCount; ch++)
            {
                var s = _channelStats.Get(ch);
                perChannel[ch] = new RollingStatsSnapshot(s.Count, s.Mean, s.Std, s.Min, s.Max);
            }
            bool tapped = _tapPending;
            _tapPending = false;
            return new CaptureSnapshot(
                Packets: _packets,
                BadCrc: _badCrc,
                BadVersion: _badVersion,
                Drops: _drops,
                ResyncBytes: _resyncBytes,
                Samples: _samples,
                Stm32RateHz: _rate.Stm32RateHz() ?? 0.0,
                HostRateHz: _rate.HostRateHz() ?? 0.0,
                ChannelStats: perChannel,
                DeviceTapped: tapped);
        }
    }

    private void ReadLoop(CancellationToken token)
    {
        var framer = new Framer();
        var buf = new byte[4096];
        while (!token.IsCancellationRequested)
        {
            int n;
            try { n = _port!.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch { break; }
            if (n <= 0) continue;

            // Mirror the raw chunk into the recording file before framing.
            // This preserves the on-the-wire bytes exactly (including any
            // resync noise), so a recorded file replays identically.
            var rec = _recordingStream;
            if (rec is not null)
            {
                try { rec.Write(buf, 0, n); }
                catch { /* swallow; recording failure must not break capture */ }
            }

            foreach (var ev in framer.Feed(buf.AsMemory(0, n)))
            {
                HandleEvent(ev);
            }
        }
        var trailing = framer.FlushResync();
        if (trailing is not null) HandleEvent(trailing);
    }

    private void HandleEvent(IFrameEvent ev)
    {
        switch (ev)
        {
            case PacketReceivedEvent pr:
            {
                var p = pr.Packet;
                lock (_lock)
                {
                    _packets++;
                    _samples += p.Header.Count;
                    // FLAGS bit 0 = the user tapped the device screen. Latch
                    // it so the GUI can surface each tap (e.g. jump to the
                    // live view) on its next poll.
                    if ((p.Header.Flags & 0x01) != 0) _tapPending = true;
                    _dropTracker.Observe(p.Header.Seq);
                    _drops = _dropTracker.Drops;
                    _rate.Observe(p.Header.TimeUs, p.Header.Count, _wall!.Elapsed.TotalSeconds);
                    for (int i = 0; i < p.Samples.Length; i++)
                    {
                        int ch = i % _channelCount;
                        ushort v = p.Samples[i];
                        _channelStats.Add(ch, v);
                        _waveform.Append(ch, v);
                        _histogram.Add(ch, v);
                        if (ch == 0) _harvest?.Feed(v);
                    }
                }
                break;
            }
            case BadCrcEvent: lock (_lock) { _badCrc++; } break;
            case BadVersionEvent: lock (_lock) { _badVersion++; } break;
            case ResyncDroppedEvent rd: lock (_lock) { _resyncBytes += rd.Bytes; } break;
        }
    }

    public void Dispose() => Stop();
}
