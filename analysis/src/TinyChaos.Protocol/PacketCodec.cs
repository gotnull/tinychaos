using System.Buffers.Binary;

namespace TinyChaos.Protocol;

/// <summary>
/// Static encode/decode for tinychaos packets.
///
/// The wire format is fully specified in docs/ENTROPY_CAPTURE_PIPELINE.md
/// section 8 and mirrored in the Python and firmware reference
/// implementations.
/// </summary>
public static class PacketCodec
{
    /// <summary>
    /// Encode a packet to a freshly allocated byte array.
    /// </summary>
    /// <param name="seq">Monotonically increasing sequence number.</param>
    /// <param name="timeUs">STM32-side microsecond timestamp.</param>
    /// <param name="samples">Per-channel-interleaved ADC samples.</param>
    /// <param name="version">Protocol version, default 1.</param>
    /// <param name="flags">Reserved flags, default 0.</param>
    public static byte[] Encode(
        uint seq,
        uint timeUs,
        ReadOnlySpan<ushort> samples,
        byte version = ProtocolConstants.ProtocolVersion,
        byte flags = ProtocolConstants.DefaultFlags)
    {
        if (samples.Length > ushort.MaxValue)
        {
            throw new ArgumentException(
                $"sample count {samples.Length} exceeds uint16 range",
                nameof(samples));
        }

        int total = ProtocolConstants.HeaderSize + 2 * samples.Length + ProtocolConstants.CrcSize;
        byte[] buf = new byte[total];

        buf[0] = ProtocolConstants.Magic0;
        buf[1] = ProtocolConstants.Magic1;
        buf[2] = version;
        buf[3] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), timeUs);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), (ushort)samples.Length);

        Span<byte> body = buf.AsSpan(ProtocolConstants.HeaderSize, 2 * samples.Length);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.Slice(2 * i, 2), samples[i]);
        }

        // CRC over VERSION through last sample (12 + 2*count bytes), i.e.
        // bytes [2, ProtocolConstants.HeaderSize + 2 * samples.Length).
        int crcScopeStart = 2;
        int crcScopeLen = (ProtocolConstants.HeaderSize - 2) + 2 * samples.Length;
        ushort crc = Crc16.Compute(buf.AsSpan(crcScopeStart, crcScopeLen));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buf.AsSpan(ProtocolConstants.HeaderSize + 2 * samples.Length, 2),
            crc);

        return buf;
    }

    /// <summary>
    /// Decode a complete packet from <paramref name="buf"/>. <paramref name="buf"/>
    /// must be the bytes from MAGIC through CRC inclusive, with no leading
    /// or trailing junk. The streaming <see cref="Framer"/> is the right way
    /// to get well-aligned slices; this method does not search for MAGIC.
    /// </summary>
    /// <exception cref="ShortPacketException">Buffer is too short.</exception>
    /// <exception cref="BadMagicException">First two bytes are not MAGIC.</exception>
    /// <exception cref="UnsupportedVersionException">Version byte is unsupported.</exception>
    /// <exception cref="BadCrcException">CRC check failed.</exception>
    public static Packet Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < ProtocolConstants.MinPacketSize)
        {
            throw new ShortPacketException(
                $"packet too short: {buf.Length} bytes, need at least " +
                $"{ProtocolConstants.MinPacketSize}");
        }

        if (buf[0] != ProtocolConstants.Magic0 || buf[1] != ProtocolConstants.Magic1)
        {
            throw new BadMagicException(
                $"bad magic: got 0x{buf[0]:X2} 0x{buf[1]:X2}, " +
                $"expected 0x{ProtocolConstants.Magic0:X2} " +
                $"0x{ProtocolConstants.Magic1:X2}");
        }

        byte version = buf[2];
        if (version != ProtocolConstants.ProtocolVersion)
        {
            throw new UnsupportedVersionException(version);
        }

        byte flags = buf[3];
        uint seq = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(4, 4));
        uint timeUs = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(8, 4));
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(12, 2));

        int expectedLen = ProtocolConstants.HeaderSize + 2 * count + ProtocolConstants.CrcSize;
        if (buf.Length < expectedLen)
        {
            throw new ShortPacketException(
                $"truncated: have {buf.Length} bytes, need {expectedLen} (count={count})");
        }

        int samplesStart = ProtocolConstants.HeaderSize;
        int samplesEnd = samplesStart + 2 * count;
        var samples = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(samplesStart + 2 * i, 2));
        }

        int crcScopeStart = 2;
        int crcScopeLen = (ProtocolConstants.HeaderSize - 2) + 2 * count;
        ushort crcComputed = Crc16.Compute(buf.Slice(crcScopeStart, crcScopeLen));
        ushort crcReceived = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(samplesEnd, 2));

        if (crcReceived != crcComputed)
        {
            throw new BadCrcException(crcComputed, crcReceived);
        }

        var header = new PacketHeader(version, flags, seq, timeUs, count);
        return new Packet(header, samples);
    }
}
