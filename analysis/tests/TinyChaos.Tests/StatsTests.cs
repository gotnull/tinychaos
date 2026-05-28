using TinyChaos.Host.Stats;
using Xunit;

namespace TinyChaos.Tests;

public class StatsTests
{
    [Fact]
    public void RollingStats_EmptyDefaults()
    {
        var s = new RollingStats();
        Assert.Equal(0, s.Count);
        Assert.Equal(0.0, s.Mean);
        Assert.Equal(0.0, s.Variance);
    }

    [Fact]
    public void RollingStats_MatchesNumpyEquivalent()
    {
        var s = new RollingStats();
        var rng = new Random(123);
        var data = new double[10_000];
        for (int i = 0; i < data.Length; i++)
        {
            // Box-Muller for a Gaussian-ish sample.
            double u1 = 1.0 - rng.NextDouble();
            double u2 = 1.0 - rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            data[i] = 2000.0 + 50.0 * z;
            s.Add(data[i]);
        }
        double mean = data.Average();
        double sumSq = data.Sum(x => (x - mean) * (x - mean));
        double sampleVar = sumSq / (data.Length - 1);
        Assert.Equal(mean, s.Mean, 6);
        Assert.Equal(Math.Sqrt(sampleVar), s.Std, 6);
        Assert.Equal(data.Min(), s.Min);
        Assert.Equal(data.Max(), s.Max);
    }

    [Fact]
    public void DropTracker_NoDrops()
    {
        var d = new DropTracker();
        for (uint i = 0; i < 100; i++) Assert.Equal(0u, d.Observe(i));
        Assert.Equal(0, d.Drops);
        Assert.Equal(100, d.Packets);
    }

    [Fact]
    public void DropTracker_SimpleGap()
    {
        var d = new DropTracker();
        d.Observe(0);
        d.Observe(1);
        Assert.Equal(3u, d.Observe(5)); // skipped 2, 3, 4
        Assert.Equal(3, d.Drops);
    }

    [Fact]
    public void DropTracker_Uint32Wraparound()
    {
        var d = new DropTracker();
        d.Observe(0xFFFFFFFEu);
        d.Observe(0xFFFFFFFFu);
        Assert.Equal(0u, d.Observe(0u));     // wrap
        Assert.Equal(0, d.Drops);
        Assert.Equal(1u, d.Observe(2u));     // skipped 1
    }

    [Fact]
    public void DropTracker_IgnoresOutOfOrder()
    {
        var d = new DropTracker();
        d.Observe(10);
        Assert.Equal(0u, d.Observe(5));
        Assert.Equal(0, d.Drops);
    }

    [Fact]
    public void RateEstimator_ConstantRate()
    {
        var r = new RateEstimator(64);
        uint t = 0;
        double host = 0.0;
        for (int i = 0; i < 32; i++)
        {
            r.Observe(t, 256, host);
            t += 25600;          // 256 samples at 10 kHz -> 25.6 ms = 25600 us
            host += 0.0256;
        }
        Assert.NotNull(r.Stm32RateHz());
        Assert.InRange(r.Stm32RateHz()!.Value, 9_999.0, 10_001.0);
        Assert.NotNull(r.HostRateHz());
    }

    [Fact]
    public void RateEstimator_ReturnsNullBelowTwoSamples()
    {
        var r = new RateEstimator();
        Assert.Null(r.Stm32RateHz());
        Assert.Null(r.HostRateHz());
        r.Observe(0, 256, 1.0);
        Assert.Null(r.Stm32RateHz());
    }
}
