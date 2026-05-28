namespace TinyChaos.Protocol;

/// <summary>
/// Decoded packet header, in the order fields appear on the wire.
/// </summary>
public readonly record struct PacketHeader(
    byte Version,
    byte Flags,
    uint Seq,
    uint TimeUs,
    ushort Count);

/// <summary>
/// A fully decoded, CRC-validated entropy capture packet.
/// </summary>
public readonly record struct Packet(PacketHeader Header, ushort[] Samples)
{
    public uint Seq => Header.Seq;
    public uint TimeUs => Header.TimeUs;
    public ushort Count => Header.Count;
}

/// <summary>
/// Protocol-level constants. Shared with the Python and C reference
/// implementations; see docs/ENTROPY_CAPTURE_PIPELINE.md section 8.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>Two-byte magic: 0xDA, 0x7A.</summary>
    public const byte Magic0 = 0xDA;
    public const byte Magic1 = 0x7A;

    public const byte ProtocolVersion = 1;
    public const byte DefaultFlags = 0;

    /// <summary>Bytes from start of MAGIC through end of COUNT, exclusive of CRC.</summary>
    public const int HeaderSize = 14;

    /// <summary>Bytes consumed by the trailing CRC field.</summary>
    public const int CrcSize = 2;

    /// <summary>Minimum valid packet size (zero samples).</summary>
    public const int MinPacketSize = HeaderSize + CrcSize;

    /// <summary>
    /// Safety bound on the COUNT field. Any header claiming more samples
    /// than this is treated as corruption and triggers resync. The maximum
    /// expected production value is around 1024; 4096 is generous headroom.
    /// </summary>
    public const int MaxSampleCount = 4096;
}
