using System.Text;
using TinyChaos.Protocol;
using Xunit;

namespace TinyChaos.Tests;

public class Crc16Tests
{
    [Fact]
    public void KnownAnswer_123456789_IsExpected()
    {
        Assert.Equal((ushort)0x29B1, Crc16.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Theory]
    [InlineData(new byte[] { }, (ushort)0xFFFF)]            // empty -> init
    [InlineData(new byte[] { 0x00 }, (ushort)0xE1F0)]
    [InlineData(new byte[] { 0xFF }, (ushort)0xFF00)]
    [InlineData(new byte[] { (byte)'A' }, (ushort)0xB915)]
    [InlineData(new byte[] { (byte)'A', (byte)'B' }, (ushort)0x4B74)]
    public void ParametrisedKnownVectors(byte[] data, ushort expected)
    {
        Assert.Equal(expected, Crc16.Compute(data));
    }

    [Fact]
    public void SingleBitFlip_ChangesCrc()
    {
        var payload = Encoding.ASCII.GetBytes("the quick brown fox jumps over the lazy dog");
        ushort baseCrc = Crc16.Compute(payload);
        for (int bit = 0; bit < payload.Length * 8; bit++)
        {
            var flipped = (byte[])payload.Clone();
            flipped[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.NotEqual(baseCrc, Crc16.Compute(flipped));
        }
    }
}
