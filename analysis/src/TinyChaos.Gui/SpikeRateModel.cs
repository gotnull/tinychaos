using System;
using System.Threading;

namespace TinyChaos.Gui;

/// <summary>A point-in-time snapshot of recent spike activity on the zener
/// channel. <see cref="Up"/>/<see cref="Down"/> are spike counts within the last
/// <see cref="Window"/> samples (<see cref="Filled"/> = how many of that window
/// have actually been seen yet). <see cref="Percent"/> is the spike density - the
/// single number to watch while tuning the circuit: higher means more avalanche
/// activity.</summary>
public readonly record struct SpikeRateSnapshot(long Up, long Down, int Filled, int Window)
{
    public long Total => Up + Down;
    public double Percent => Filled > 0 ? 100.0 * Total / Filled : 0.0;
}

/// <summary>
/// Continuously measures how often the zener (channel 0) crosses above/below its
/// rolling noise baseline - the same Z-score spike test the entropy harvest uses
/// (<see cref="EntropyHarvestModel"/>), so the rate reflects exactly what harvest
/// would capture. Unlike harvest, this is always-on (no file) and tracks spike
/// counts over a sliding window of the most recent <c>rateWindow</c> samples, so
/// the operator sees a live "how good is my circuit" statistic.
///
/// O(1) per sample: a circular baseline buffer for the rolling mean/std, plus a
/// circular buffer of per-sample classifications (none/up/down) for the sliding
/// counts. <see cref="Feed"/> is single-threaded (capture read thread);
/// <see cref="Snapshot"/> is safe to read concurrently from the UI thread.
/// </summary>
public sealed class SpikeRateModel
{
    private const byte None = 0, Up = 1, Down = 2;

    private readonly int _baselineWindow;
    private readonly double _threshold;
    private readonly int _rateWindow;

    // Rolling baseline (mean/variance) over the most recent _baselineWindow samples.
    private readonly double[] _ring;
    private int _ringPos, _ringCount;
    private double _runSum, _runSumSq;

    // Sliding window of per-sample classifications, with running up/down counts.
    private readonly byte[] _cls;
    private int _clsPos, _clsCount;
    private long _up, _down;

    public SpikeRateModel(int baselineWindow = 512, double threshold = 2.5, int rateWindow = 10000)
    {
        if (baselineWindow < 4) throw new ArgumentOutOfRangeException(nameof(baselineWindow));
        if (threshold <= 0.0) throw new ArgumentOutOfRangeException(nameof(threshold));
        if (rateWindow < 1) throw new ArgumentOutOfRangeException(nameof(rateWindow));
        _baselineWindow = baselineWindow;
        _threshold = threshold;
        _rateWindow = rateWindow;
        _ring = new double[baselineWindow];
        _cls = new byte[rateWindow];
    }

    /// <summary>Clear all state (e.g. when a new capture starts).</summary>
    public void Reset()
    {
        Array.Clear(_ring, 0, _ring.Length);
        _ringPos = _ringCount = 0;
        _runSum = _runSumSq = 0.0;
        Array.Clear(_cls, 0, _cls.Length);
        _clsPos = _clsCount = 0;
        Interlocked.Exchange(ref _up, 0);
        Interlocked.Exchange(ref _down, 0);
    }

    /// <summary>Feed one channel-0 (zener) ADC sample.</summary>
    public void Feed(ushort sample)
    {
        double x = sample;

        // Rolling baseline update (identical maths to EntropyHarvestModel).
        if (_ringCount < _baselineWindow)
        {
            _ring[_ringPos] = x;
            _runSum += x;
            _runSumSq += x * x;
            _ringCount++;
        }
        else
        {
            double evicted = _ring[_ringPos];
            _runSum += x - evicted;
            _runSumSq += x * x - evicted * evicted;
            _ring[_ringPos] = x;
        }
        _ringPos = (_ringPos + 1) % _baselineWindow;

        // Classify this sample (none until the baseline is trustworthy).
        byte c = None;
        if (_ringCount >= _baselineWindow / 2)
        {
            double mean = _runSum / _ringCount;
            double variance = Math.Max(0.0, _runSumSq / _ringCount - mean * mean);
            double sigma = Math.Sqrt(variance);
            if (sigma >= 1.0)
            {
                double z = (x - mean) / sigma;
                if (z >= _threshold) c = Up;
                else if (z <= -_threshold) c = Down;
            }
        }

        // Slide it into the rate window: drop the evicted classification's count,
        // add the new one. The buffer starts all-None, so the fill phase evicts
        // None and decrements nothing.
        byte old = _cls[_clsPos];
        if (old == Up) Interlocked.Decrement(ref _up);
        else if (old == Down) Interlocked.Decrement(ref _down);

        _cls[_clsPos] = c;
        if (c == Up) Interlocked.Increment(ref _up);
        else if (c == Down) Interlocked.Increment(ref _down);

        _clsPos = (_clsPos + 1) % _rateWindow;
        if (_clsCount < _rateWindow) _clsCount++;
    }

    public SpikeRateSnapshot Snapshot() => new(
        Interlocked.Read(ref _up), Interlocked.Read(ref _down),
        Volatile.Read(ref _clsCount), _rateWindow);
}
