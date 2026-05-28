using TinyChaos.Protocol;
using Xunit;

namespace TinyChaos.Tests;

public class FramerTests
{
    private static byte[] MakePacket(uint seq, uint timeUs, int sampleCount, int seed)
    {
        var rng = new Random(seed);
        var samples = new ushort[sampleCount];
        for (int i = 0; i < sampleCount; i++) samples[i] = (ushort)rng.Next(0, 4096);
        return PacketCodec.Encode(seq, timeUs, samples);
    }

    private static List<IFrameEvent> Drive(byte[] stream, int[]? chunkSizes = null)
    {
        var framer = new Framer();
        var events = new List<IFrameEvent>();
        if (chunkSizes is null)
        {
            events.AddRange(framer.Feed(stream));
        }
        else
        {
            int i = 0, j = 0;
            while (i < stream.Length)
            {
                int size = Math.Max(1, chunkSizes[j % chunkSizes.Length]);
                int end = Math.Min(i + size, stream.Length);
                events.AddRange(framer.Feed(stream.AsMemory(i, end - i)));
                i = end;
                j++;
            }
        }
        var trailing = framer.FlushResync();
        if (trailing is not null) events.Add(trailing);
        return events;
    }

    [Fact]
    public void SingleCleanPacket()
    {
        var pkt = MakePacket(0u, 0u, 4, 1);
        var events = Drive(pkt);
        Assert.Single(events);
        Assert.IsType<PacketReceivedEvent>(events[0]);
        Assert.Equal(0u, ((PacketReceivedEvent)events[0]).Packet.Header.Seq);
    }

    [Fact]
    public void MultipleCleanPackets()
    {
        var packets = new byte[5][];
        for (int i = 0; i < 5; i++) packets[i] = MakePacket((uint)i, (uint)(i * 1000), 64, i);
        var stream = packets.SelectMany(p => p).ToArray();
        var received = Drive(stream).OfType<PacketReceivedEvent>().ToList();
        Assert.Equal(5, received.Count);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, received.Select(e => e.Packet.Header.Seq));
    }

    [Fact]
    public void LeadingGarbage_Resyncs()
    {
        var rng = new Random(7);
        var garbage = new byte[73];
        rng.NextBytes(garbage);
        // Avoid accidental magic in the garbage region.
        for (int i = 0; i < garbage.Length - 1; i++)
        {
            if (garbage[i] == 0xDA && garbage[i + 1] == 0x7A) garbage[i + 1] = 0x00;
        }
        var pkt = MakePacket(0u, 0u, 4, 11);
        var stream = garbage.Concat(pkt).ToArray();
        var events = Drive(stream);
        Assert.Single(events.OfType<PacketReceivedEvent>());
        int totalResync = events.OfType<ResyncDroppedEvent>().Sum(e => e.Bytes);
        Assert.Equal(garbage.Length, totalResync);
    }

    [Fact]
    public void BadCrc_OnOnePacket_RestRecovered()
    {
        var packets = new byte[5][];
        for (int i = 0; i < 5; i++) packets[i] = MakePacket((uint)i, (uint)(i * 1000), 64, i);
        // Corrupt the sample region of the middle packet.
        packets[2][20] ^= 0x01;
        var stream = packets.SelectMany(p => p).ToArray();
        var events = Drive(stream);
        var received = events.OfType<PacketReceivedEvent>().ToList();
        Assert.Equal(4, received.Count);
        Assert.Single(events.OfType<BadCrcEvent>());
    }

    [Fact]
    public void TruncatedLastPacket_PreviousPacketsReceived()
    {
        var packets = new byte[4][];
        for (int i = 0; i < 4; i++) packets[i] = MakePacket((uint)i, (uint)(i * 1000), 64, i);
        var prefix = packets.Take(3).SelectMany(p => p).ToArray();
        var truncated = packets[3].AsSpan(0, packets[3].Length / 2).ToArray();
        var stream = prefix.Concat(truncated).ToArray();
        var received = Drive(stream).OfType<PacketReceivedEvent>().ToList();
        Assert.Equal(3, received.Count);
    }

    [Fact]
    public void SeqGap_PassedThroughForConsumerToDetect()
    {
        var packets = new byte[5][];
        for (int i = 0; i < 5; i++) packets[i] = MakePacket((uint)i, (uint)(i * 1000), 8, i);
        // Drop the middle packet.
        var stream = packets.Take(2).Concat(packets.Skip(3)).SelectMany(p => p).ToArray();
        var seqs = Drive(stream)
            .OfType<PacketReceivedEvent>()
            .Select(e => e.Packet.Header.Seq)
            .ToArray();
        Assert.Equal(new uint[] { 0, 1, 3, 4 }, seqs);
    }

    [Fact]
    public void BadVersion_EmitsEventAndResyncs()
    {
        var bad = MakePacket(2u, 2u, 4, 5);
        bad[2] = 99;          // corrupt version
        var good = MakePacket(1u, 1u, 4, 6);
        var stream = bad.Concat(good).ToArray();
        var events = Drive(stream);
        Assert.Single(events.OfType<BadVersionEvent>());
        Assert.Equal((byte)99, events.OfType<BadVersionEvent>().First().Version);
        Assert.Contains(events.OfType<PacketReceivedEvent>(), e => e.Packet.Header.Seq == 1u);
    }

    [Theory]
    [InlineData(new int[] { 1 })]
    [InlineData(new int[] { 3, 7 })]
    [InlineData(new int[] { 17, 1, 64 })]
    [InlineData(new int[] { 512 })]
    public void ChunkSizeInvariance(int[] sizes)
    {
        var packets = new byte[6][];
        for (int i = 0; i < 6; i++) packets[i] = MakePacket((uint)i, (uint)(i * 1000), 16, i);
        var stream = packets.SelectMany(p => p).ToArray();

        var refSeqs = Drive(stream).OfType<PacketReceivedEvent>().Select(e => e.Packet.Header.Seq).ToArray();
        var chunkedSeqs = Drive(stream, sizes).OfType<PacketReceivedEvent>().Select(e => e.Packet.Header.Seq).ToArray();
        Assert.Equal(refSeqs, chunkedSeqs);
    }
}
