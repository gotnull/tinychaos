namespace TinyChaos.Host.Stats;

/// <summary>
/// Estimate sample rate from STM32 timestamps and from host wall-clock
/// time, over a sliding window.
/// </summary>
public sealed class RateEstimator
{
    private readonly int _windowSize;
    private readonly Queue<uint> _stmTimes;
    private readonly Queue<int> _counts;
    private readonly Queue<double> _hostTimes;

    public RateEstimator(int windowSize = 256)
    {
        _windowSize = windowSize;
        _stmTimes = new Queue<uint>(windowSize);
        _counts = new Queue<int>(windowSize);
        _hostTimes = new Queue<double>(windowSize);
    }

    public void Observe(uint timeUs, int sampleCount, double hostTimeSec)
    {
        _stmTimes.Enqueue(timeUs);
        _counts.Enqueue(sampleCount);
        _hostTimes.Enqueue(hostTimeSec);
        while (_stmTimes.Count > _windowSize)
        {
            _stmTimes.Dequeue();
            _counts.Dequeue();
            _hostTimes.Dequeue();
        }
    }

    /// <summary>STM32-derived rate in Hz, or null if too few observations.</summary>
    public double? Stm32RateHz()
    {
        if (_stmTimes.Count < 2) return null;
        var times = _stmTimes.ToArray();
        var counts = _counts.ToArray();
        long samples = 0;
        for (int i = 1; i < counts.Length; i++) samples += counts[i];
        long totalUs = 0;
        for (int i = 1; i < times.Length; i++)
        {
            uint delta = unchecked(times[i] - times[i - 1]);
            if (delta > 0x7FFFFFFFu) return null;
            totalUs += delta;
        }
        if (totalUs == 0) return null;
        return samples * 1_000_000.0 / totalUs;
    }

    /// <summary>Host-derived rate in Hz, or null if too few observations.</summary>
    public double? HostRateHz()
    {
        if (_hostTimes.Count < 2) return null;
        var times = _hostTimes.ToArray();
        var counts = _counts.ToArray();
        long samples = 0;
        for (int i = 1; i < counts.Length; i++) samples += counts[i];
        double elapsed = times[^1] - times[0];
        if (elapsed <= 0) return null;
        return samples / elapsed;
    }
}
