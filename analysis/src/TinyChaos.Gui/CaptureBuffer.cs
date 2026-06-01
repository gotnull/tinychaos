using System;
using System.Collections.Generic;
using System.IO;
using TinyChaos.Protocol;

namespace TinyChaos.Gui;

/// <summary>
/// A whole decoded capture held in memory for the saved-data viewer. Unlike the
/// live <see cref="WaveformModel"/> (a small rolling window), this keeps every
/// sample of the file so the viewer can pan and zoom across the full recording.
///
/// Decoding goes through the same <see cref="Framer"/> the live pipeline uses,
/// so it is CRC-validated and resync-safe (corrupted packets are dropped, not
/// fed in as garbage). Samples are channel-interleaved on the wire
/// (ch0, ch1, ch0, ch1, ...) and de-interleaved here into one array per channel.
/// </summary>
public sealed class CaptureBuffer
{
    private readonly ushort[][] _channels;

    /// <summary>Number of channels (de-interleaved).</summary>
    public int ChannelCount { get; }

    /// <summary>Samples per channel (all channels normalised to the same length).</summary>
    public int Length { get; }

    /// <summary>Source file, for display.</summary>
    public string SourcePath { get; }

    /// <summary>Decoded packet count, for the status line.</summary>
    public int PacketCount { get; }

    private CaptureBuffer(string path, ushort[][] channels, int length, int packets)
    {
        SourcePath = path;
        _channels = channels;
        ChannelCount = channels.Length;
        Length = length;
        PacketCount = packets;
    }

    /// <summary>Raw sample array for one channel (do not mutate).</summary>
    public ushort[] Channel(int ch) => _channels[ch];

    /// <summary>
    /// Write just the sample values - no packet headers, no CRC - as
    /// channel-interleaved little-endian uint16 (the on-wire sample order:
    /// ch0, ch1, ch0, ch1, ...). Ready to load in numpy/audio tools/etc.
    /// </summary>
    public void WriteRawInterleaved(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        var row = new byte[ChannelCount * 2];
        for (int i = 0; i < Length; i++)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                ushort v = _channels[c][i];
                row[c * 2] = (byte)(v & 0xFF);
                row[c * 2 + 1] = (byte)(v >> 8);
            }
            fs.Write(row, 0, row.Length);
        }
    }

    /// <summary>Write samples as CSV: one row per sample, columns index,channel,value.</summary>
    public void WriteCsv(string path)
    {
        using var sw = new StreamWriter(path);
        sw.Write("index,channel,value\n");
        for (int i = 0; i < Length; i++)
        {
            for (int c = 0; c < ChannelCount; c++)
            {
                sw.Write(i * ChannelCount + c);
                sw.Write(',');
                sw.Write(c);
                sw.Write(',');
                sw.Write(_channels[c][i]);
                sw.Write('\n');
            }
        }
    }

    /// <summary>
    /// Decode a raw .bin capture into per-channel arrays. <paramref name="channelCount"/>
    /// must match how the firmware interleaves (2 for tinychaos: zener + baseline).
    /// </summary>
    public static CaptureBuffer Load(string path, int channelCount = 2)
    {
        if (channelCount < 1) throw new ArgumentOutOfRangeException(nameof(channelCount));

        var all = new List<ushort>(1 << 16);
        int packets = 0;
        var framer = new Framer();
        byte[] bytes = File.ReadAllBytes(path);
        foreach (var ev in framer.Feed(bytes))
        {
            if (ev is PacketReceivedEvent pr)
            {
                all.AddRange(pr.Packet.Samples);
                packets++;
            }
        }

        int len = all.Count / channelCount;          // whole samples per channel
        var chans = new ushort[channelCount][];
        for (int ch = 0; ch < channelCount; ch++)
        {
            var arr = new ushort[len];
            for (int j = 0; j < len; j++)
            {
                arr[j] = all[j * channelCount + ch];  // de-interleave
            }
            chans[ch] = arr;
        }
        return new CaptureBuffer(path, chans, len, packets);
    }
}
