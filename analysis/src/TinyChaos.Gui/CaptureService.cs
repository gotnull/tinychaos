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
    RollingStatsSnapshot[] ChannelStats);

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
    private readonly DropTracker _dropTracker = new();
    private readonly RateEstimator _rate = new();
    private readonly ChannelStats _channelStats;

    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Stopwatch? _wall;

    public bool IsRunning { get; private set; }

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
            return new CaptureSnapshot(
                Packets: _packets,
                BadCrc: _badCrc,
                BadVersion: _badVersion,
                Drops: _drops,
                ResyncBytes: _resyncBytes,
                Samples: _samples,
                Stm32RateHz: _rate.Stm32RateHz() ?? 0.0,
                HostRateHz: _rate.HostRateHz() ?? 0.0,
                ChannelStats: perChannel);
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
