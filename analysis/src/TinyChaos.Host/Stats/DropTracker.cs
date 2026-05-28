namespace TinyChaos.Host.Stats;

/// <summary>
/// Track packet sequence drops in a uint32 stream. Handles uint32
/// wraparound and ignores out-of-order packets (treated as zero drops).
/// </summary>
public sealed class DropTracker
{
    private uint? _expected;
    private long _drops;
    private long _packets;

    public long Drops => _drops;
    public long Packets => _packets;

    /// <summary>
    /// Observe a sequence number. Returns the number of drops attributable
    /// to this observation (zero on the first call, or for out-of-order
    /// packets).
    /// </summary>
    public uint Observe(uint seq)
    {
        _packets++;
        if (_expected is null)
        {
            _expected = unchecked(seq + 1u);
            return 0;
        }
        uint expected = _expected.Value;
        uint gap = unchecked(seq - expected);
        if (gap > 0x7FFFFFFFu)
        {
            // Out-of-order / replayed packet. Ignore.
            return 0;
        }
        _drops += gap;
        _expected = unchecked(seq + 1u);
        return gap;
    }
}
