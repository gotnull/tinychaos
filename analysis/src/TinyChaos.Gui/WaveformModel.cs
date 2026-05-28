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
}
