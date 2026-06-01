using System;
using System.Threading;

namespace TinyChaos.Gui;

/// <summary>
/// Cumulative per-channel histogram. ADC code -> count of times that code
/// has been observed since the capture started.
/// </summary>
public sealed class HistogramModel
{
    private long[][] _counts;
    private readonly object _lock = new();
    private int _bins;

    public int ChannelCount { get; }

    /// <summary>Current bin count (1 &lt;&lt; bits): 4096 for 12-bit, 65536 for 16-bit.</summary>
    public int Bins => Volatile.Read(ref _bins);

    public HistogramModel(int channelCount, int bits)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (bits < 1 || bits > 16) throw new ArgumentOutOfRangeException(nameof(bits));
        ChannelCount = channelCount;
        _bins = 1 << bits;
        _counts = new long[channelCount][];
        for (int i = 0; i < channelCount; i++) _counts[i] = new long[_bins];
    }

    /// <summary>
    /// Resize the histogram to a new resolution (12 or 16 bit) in place, keeping
    /// the same instance so the capture pipeline's reference stays valid. Drops
    /// existing counts (the old bins no longer map). Thread-safe against Add.
    /// </summary>
    public void Reconfigure(int bits)
    {
        if (bits < 1 || bits > 16) throw new ArgumentOutOfRangeException(nameof(bits));
        int newBins = 1 << bits;
        lock (_lock)
        {
            if (newBins == _bins) { return; }
            var fresh = new long[ChannelCount][];
            for (int i = 0; i < ChannelCount; i++) fresh[i] = new long[newBins];
            _counts = fresh;
            Volatile.Write(ref _bins, newBins);
        }
    }

    public void Add(int channelIndex, ushort sample)
    {
        lock (_lock)
        {
            // Compute the bin inside the lock so a concurrent Reconfigure can't
            // leave us indexing past the (possibly just-resized) array.
            int bin = sample < _bins ? sample : _bins - 1;
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
