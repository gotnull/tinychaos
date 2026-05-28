using System;

namespace TinyChaos.Gui;

/// <summary>
/// Cumulative per-channel histogram. ADC code -> count of times that code
/// has been observed since the capture started.
/// </summary>
public sealed class HistogramModel
{
    private readonly long[][] _counts;
    private readonly object _lock = new();

    public int ChannelCount { get; }
    public int Bins { get; }

    public HistogramModel(int channelCount, int bits)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (bits < 1 || bits > 16) throw new ArgumentOutOfRangeException(nameof(bits));
        ChannelCount = channelCount;
        Bins = 1 << bits;
        _counts = new long[channelCount][];
        for (int i = 0; i < channelCount; i++) _counts[i] = new long[Bins];
    }

    public void Add(int channelIndex, ushort sample)
    {
        int bin = sample < Bins ? sample : Bins - 1;
        lock (_lock)
        {
            _counts[channelIndex][bin]++;
        }
    }

    public long[] Snapshot(int channelIndex)
    {
        lock (_lock)
        {
            var src = _counts[channelIndex];
            var copy = new long[src.Length];
            Array.Copy(src, copy, src.Length);
            return copy;
        }
    }

    /// <summary>Drop all counts on every channel.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                Array.Clear(_counts[ch], 0, _counts[ch].Length);
            }
        }
    }
}
