using TinyChaos.Protocol;
using Xunit;

namespace TinyChaos.Tests;

public class PacketTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(256)]
    [InlineData(1024)]
    public void Roundtrip_VariousSampleCounts(int count)
    {
        var rng = new Random(count);
        var samples = new ushort[count];
        for (int i = 0; i < count; i++) samples[i] = (ushort)rng.Next(0, 4096);

        byte[] encoded = PacketCodec.Encode(seq: 42u, timeUs: 12_345_678u, samples);
        Packet decoded = PacketCodec.Decode(encoded);

        Assert.Equal(1, decoded.Header.Version);
        Assert.Equal(0, decoded.Header.Flags);
        Assert.Equal(42u, decoded.Header.Seq);
        Assert.Equal(12_345_678u, decoded.Header.TimeUs);
        Assert.Equal((ushort)count, decoded.Header.Count);
        Assert.Equal(samples, decoded.Samples);
    }

    [Fact]
    public void ExplicitByteLayout_MatchesSpec()
    {
        var samples = new ushort[] { 0x0102, 0x0304 };
        byte[] encoded = PacketCodec.Encode(0x11223344u, 0x55667788u, samples);

        Assert.Equal(0xDA, encoded[0]);
        Assert.Equal(0x7A, encoded[1]);
        Assert.Equal(1, encoded[2]);          // version
        Assert.Equal(0, encoded[3]);          // flags
        // SEQ LE 0x11223344 -> 44 33 22 11
        Assert.Equal(0x44, encoded[4]);
        Assert.Equal(0x33, encoded[5]);
        Assert.Equal(0x22, encoded[6]);
        Assert.Equal(0x11, encoded[7]);
        // TIME_US LE 0x55667788 -> 88 77 66 55
        Assert.Equal(0x88, encoded[8]);
        Assert.Equal(0x77, encoded[9]);
        Assert.Equal(0x66, encoded[10]);
        Assert.Equal(0x55, encoded[11]);
        // COUNT LE 2 -> 02 00
        Assert.Equal(0x02, encoded[12]);
        Assert.Equal(0x00, encoded[13]);
        // SAMPLES LE 0x0102 0x0304 -> 02 01 04 03
        Assert.Equal(0x02, encoded[14]);
        Assert.Equal(0x01, encoded[15]);
        Assert.Equal(0x04, encoded[16]);
        Assert.Equal(0x03, encoded[17]);
        // Total length: 14 header + 2*2 samples + 2 CRC = 20
        Assert.Equal(20, encoded.Length);
    }

    [Fact]
    public void ParityWithPythonAndFirmware_FixedInput()
    {
        // Cross-implementation byte-level parity. The Python reference and
        // the firmware C implementation both produce this exact byte
        // sequence for the same input.
        byte[] encoded = PacketCodec.Encode(
            seq: 0x11223344u,
            timeUs: 0x55667788u,
            new ushort[] { 0x0102, 0x0304, 0x0506, 0x0708 });
        string hex = Convert.ToHexString(encoded);
        Assert.Equal("DA7A0100443322118877665504000201040306050807924D", hex);
    }

    [Fact]
    public void Reject_ShortBuffer()
    {
        Assert.Throws<ShortPacketException>(() => PacketCodec.Decode(Array.Empty<byte>()));
        Assert.Throws<ShortPacketException>(() => PacketCodec.Decode(new byte[] { 0xDA, 0x7A }));
    }

    [Fact]
    public void Reject_BadMagic()
    {
        byte[] payload = PacketCodec.Encode(1u, 1u, new ushort[] { 0, 1, 2, 3 });
        payload[0] = 0x00;
        Assert.Throws<BadMagicException>(() => PacketCodec.Decode(payload));
    }

    [Fact]
    public void Reject_BadCrc()
    {
        byte[] payload = PacketCodec.Encode(1u, 1u, new ushort[] { 0, 1, 2, 3 });
        payload[^1] ^= 0x01;
        Assert.Throws<BadCrcException>(() => PacketCodec.Decode(payload));
    }

    [Fact]
    public void Reject_UnsupportedVersion()
    {
        byte[] payload = PacketCodec.Encode(1u, 1u, new ushort[] { 0, 1, 2, 3 });
        payload[2] = 99;
        var ex = Assert.Throws<UnsupportedVersionException>(() => PacketCodec.Decode(payload));
        Assert.Equal((byte)99, ex.Version);
    }

    [Fact]
    public void Reject_TruncatedSamples()
    {
        byte[] full = PacketCodec.Encode(1u, 1u, new ushort[] { 0, 1, 2, 3, 4, 5 });
        var truncated = full.AsSpan(0, ProtocolConstants.HeaderSize + 4).ToArray();
        Assert.Throws<ShortPacketException>(() => PacketCodec.Decode(truncated));
    }
}
