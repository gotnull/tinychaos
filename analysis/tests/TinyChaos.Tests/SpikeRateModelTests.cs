using TinyChaos.Gui;
using Xunit;

namespace TinyChaos.Tests;

public class SpikeRateModelTests
{
    // A quiet but non-flat baseline: alternating +/-2 about 2048 gives sigma ~2,
    // so each sample's Z-score is ~1 (below the 2.5 threshold) - no spikes.
    private static void FeedBaseline(SpikeRateModel m, int n)
    {
        for (int i = 0; i < n; i++) m.Feed((ushort)(i % 2 == 0 ? 2046 : 2050));
    }

    [Fact]
    public void Percent_Is_Spike_Density_Over_Filled()
    {
        var s = new SpikeRateSnapshot(Up: 6, Down: 4, Filled: 1000, Window: 10000);
        Assert.Equal(10, s.Total);
        Assert.Equal(1.0, s.Percent, 3);   // 10 / 1000 * 100
    }

    [Fact]
    public void EmptySnapshot_IsZero()
    {
        var m = new SpikeRateModel(64, 2.5, 1000);
        var s = m.Snapshot();
        Assert.Equal(0, s.Filled);
        Assert.Equal(0, s.Total);
        Assert.Equal(0.0, s.Percent);
    }

    [Fact]
    public void QuietBaseline_ReadsAlmostNoSpikes()
    {
        var m = new SpikeRateModel(baselineWindow: 64, threshold: 2.5, rateWindow: 10_000);
        FeedBaseline(m, 3000);
        var s = m.Snapshot();
        Assert.True(s.Filled > 0);
        Assert.True(s.Percent < 5.0, $"quiet baseline should read low, got {s.Percent}%");
    }

    [Fact]
    public void UpAndDownExcursions_CountedByDirection()
    {
        var m = new SpikeRateModel(baselineWindow: 64, threshold: 2.5, rateWindow: 1_000_000);
        FeedBaseline(m, 200);                       // establish baseline
        for (int k = 0; k < 30; k++)
        {
            m.Feed(2400);                            // clearly above -> up spike
            FeedBaseline(m, 40);
            m.Feed(1700);                            // clearly below -> down spike
            FeedBaseline(m, 40);
        }
        var s = m.Snapshot();
        Assert.True(s.Up > 0, "expected up spikes");
        Assert.True(s.Down > 0, "expected down spikes");
    }

    [Fact]
    public void SlidingWindow_CapsFilledAndTotal()
    {
        var m = new SpikeRateModel(baselineWindow: 64, threshold: 2.5, rateWindow: 500);
        FeedBaseline(m, 5000);
        var s = m.Snapshot();
        Assert.Equal(500, s.Filled);                // never exceeds the window
        Assert.True(s.Total <= s.Filled);
    }

    [Fact]
    public void Reset_ClearsAllCounts()
    {
        var m = new SpikeRateModel(baselineWindow: 64, threshold: 2.5, rateWindow: 1000);
        FeedBaseline(m, 100);
        for (int k = 0; k < 20; k++) { m.Feed(2400); FeedBaseline(m, 10); }
        m.Reset();
        var s = m.Snapshot();
        Assert.Equal(0, s.Up);
        Assert.Equal(0, s.Down);
        Assert.Equal(0, s.Filled);
    }
}
