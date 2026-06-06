using System;

namespace TinyChaos.Gui;

/// <summary>
/// Per-channel rolling sample buffers. Single producer (capture service)
/// and single consumer (UI render). Producer writes under a lock; consumer
/// takes a snapshot copy when it draws.
/// </summary>
public sealed class WaveformModel
{
    private readonly ushort[][] _buffers;
    private readonly int[] _heads;
    private readonly int[] _counts;
    private readonly object _lock = new();

    public int ChannelCount { get; }
    public int WindowSamples { get; }

    public WaveformModel(int channelCount, int windowSamples)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (windowSamples < 2) throw new ArgumentOutOfRangeException(nameof(windowSamples));
        ChannelCount = channelCount;
        WindowSamples = windowSamples;
        _buffers = new ushort[channelCount][];
        _heads = new int[channelCount];
        _counts = new int[channelCount];
        for (int i = 0; i < channelCount; i++) _buffers[i] = new ushort[windowSamples];
    }

    public void Append(int channelIndex, ushort sample)
    {
        lock (_lock)
        {
            var buf = _buffers[channelIndex];
            buf[_heads[channelIndex]] = sample;
            _heads[channelIndex] = (_heads[channelIndex] + 1) % buf.Length;
            if (_counts[channelIndex] < buf.Length) _counts[channelIndex]++;
        }
    }

    /// <summary>
    /// Returns the samples in time-order (oldest to newest) within the
    /// current window. May return fewer than <see cref="WindowSamples"/>
    /// before the buffer has filled.
    /// </summary>
    public ushort[] Snapshot(int channelIndex)
    {
        lock (_lock)
        {
            var buf = _buffers[channelIndex];
            int count = _counts[channelIndex];
            int head = _heads[channelIndex];
            var result = new ushort[count];
            if (count == 0) return result;
            int start = count < buf.Length ? 0 : head;
            for (int i = 0; i < count; i++)
            {
                result[i] = buf[(start + i) % buf.Length];
            }
            return result;
        }
    }

    /// <summary>Returns the observed min and max ushort across the current window, or (0, 0) if no samples.</summary>
    public (ushort Min, ushort Max) GetMinMax(int channelIndex)
    {
        lock (_lock)
        {
            int count = _counts[channelIndex];
            if (count == 0) return (0, 0);
            var buf = _buffers[channelIndex];
            int head = _heads[channelIndex];
            int start = count < buf.Length ? 0 : head;
            ushort min = ushort.MaxValue, max = ushort.MinValue;
            for (int i = 0; i < count; i++)
            {
                ushort v = buf[(start + i) % buf.Length];
                if (v < min) min = v;
                if (v > max) max = v;
            }
            return (min, max);
        }
    }

    /// <summary>Drop all samples on every channel.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                Array.Clear(_buffers[ch], 0, _buffers[ch].Length);
                _heads[ch] = 0;
                _counts[ch] = 0;
            }
        }
    }
}
