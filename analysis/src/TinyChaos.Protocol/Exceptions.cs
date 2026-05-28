namespace TinyChaos.Protocol;

/// <summary>Base class for any protocol-level decoding failure.</summary>
public class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>The buffer is shorter than the minimum or claimed packet length.</summary>
public sealed class ShortPacketException : ProtocolException
{
    public ShortPacketException(string message) : base(message) { }
}

/// <summary>The first two bytes did not match the MAGIC sequence.</summary>
public sealed class BadMagicException : ProtocolException
{
    public BadMagicException(string message) : base(message) { }
}

/// <summary>The trailing CRC did not match the computed CRC over the payload.</summary>
public sealed class BadCrcException : ProtocolException
{
    public ushort Expected { get; }
    public ushort Actual { get; }

    public BadCrcException(ushort expected, ushort actual)
        : base($"CRC mismatch: expected 0x{expected:X4}, got 0x{actual:X4}")
    {
        Expected = expected;
        Actual = actual;
    }
}

/// <summary>
/// The VERSION byte does not match a protocol version this assembly knows
/// about.
/// </summary>
public sealed class UnsupportedVersionException : ProtocolException
{
    public byte Version { get; }

    public UnsupportedVersionException(byte version)
        : base($"Unsupported protocol version: {version}")
    {
        Version = version;
    }
}
