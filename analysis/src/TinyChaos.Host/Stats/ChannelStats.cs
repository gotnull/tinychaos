namespace TinyChaos.Host.Stats;

/// <summary>
/// Maintain rolling per-channel statistics. Channels are addressed by
/// integer index.
/// </summary>
public sealed class ChannelStats
{
    private readonly RollingStats[] _stats;

    public ChannelStats(int channelCount)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));
        _stats = new RollingStats[channelCount];
        for (int i = 0; i < channelCount; i++) _stats[i] = new RollingStats();
    }

    public int ChannelCount => _stats.Length;

    public void Add(int channelIndex, ushort value) => _stats[channelIndex].Add(value);

    public RollingStats Get(int channelIndex) => _stats[channelIndex];
}
