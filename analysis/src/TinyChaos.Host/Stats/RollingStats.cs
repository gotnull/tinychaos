namespace TinyChaos.Host.Stats;

/// <summary>
/// Numerically stable running mean and variance via Welford's algorithm.
/// Tracks a global running statistic over an unbounded number of samples.
/// </summary>
public sealed class RollingStats
{
    private long _n;
    private double _mean;
    private double _m2;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;

    public long Count => _n;
    public double Mean => _mean;
    public double Variance => _n < 2 ? 0.0 : _m2 / (_n - 1);
    public double Std => Math.Sqrt(Variance);
    public double Min => _n == 0 ? double.NaN : _min;
    public double Max => _n == 0 ? double.NaN : _max;

    public void Add(double value)
    {
        _n++;
        double delta = value - _mean;
        _mean += delta / _n;
        double delta2 = value - _mean;
        _m2 += delta * delta2;
        if (value < _min) _min = value;
        if (value > _max) _max = value;
    }

    public void AddRange(ReadOnlySpan<ushort> values)
    {
        foreach (ushort v in values) Add(v);
    }
}
