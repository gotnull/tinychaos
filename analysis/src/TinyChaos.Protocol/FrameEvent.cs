namespace TinyChaos.Protocol;

/// <summary>
/// Marker interface for events produced by the streaming <see cref="Framer"/>.
/// </summary>
public interface IFrameEvent
{
}

/// <summary>A valid, CRC-checked packet.</summary>
public sealed record PacketReceivedEvent(Packet Packet) : IFrameEvent;

/// <summary>
/// A packet whose magic and length were plausible but whose CRC failed.
/// The header has been parsed so the consumer can see which SEQ value was
/// affected.
/// </summary>
public sealed record BadCrcEvent(PacketHeader Header, ushort ExpectedCrc, ushort ActualCrc) : IFrameEvent;

/// <summary>
/// A packet whose MAGIC was right but whose VERSION byte was not 1.
/// </summary>
public sealed record BadVersionEvent(byte Version) : IFrameEvent;

/// <summary>
/// One or more bytes were skipped during resync. Emitted lazily: while the
/// framer is searching for MAGIC it accumulates the count internally, and
/// emits a single event with the total just before the next
/// <see cref="PacketReceivedEvent"/> or <see cref="BadCrcEvent"/>.
/// </summary>
public sealed record ResyncDroppedEvent(int Bytes) : IFrameEvent;
