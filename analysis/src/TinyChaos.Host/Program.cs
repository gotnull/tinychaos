using System.Diagnostics;
using TinyChaos.Host.Export;
using TinyChaos.Host.Io;
using TinyChaos.Host.Stats;
using TinyChaos.Protocol;

namespace TinyChaos.Host;

internal static class Program
{
    public static int Main(string[] argv)
    {
        CliArgs args;
        try { args = CliArgs.Parse(argv); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            CliArgs.PrintHelp();
            return 2;
        }

        if (args.Replay is not null && !File.Exists(args.Replay))
        {
            Console.Error.WriteLine($"error: replay file not found: {args.Replay}");
            return 2;
        }

        IByteSource source;
        try
        {
            source = args.Replay is not null
                ? new FileSource(args.Replay)
                : new SerialSource(args.Port!, args.Baud);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: failed to open source: {ex.Message}");
            return 2;
        }

        var framer = new Framer();
        var drops = new DropTracker();
        var rate = new RateEstimator();
        var channelStats = new ChannelStats(args.Channels);

        long packets = 0, badCrc = 0, badVersion = 0, samples = 0;
        long resyncBytes = 0;

        CsvExporter? csv = args.CsvPath is null ? null
            : new CsvExporter(args.CsvPath, args.Channels, args.ValidationLabel);
        RawBinaryExporter? rawBin = args.RawBinPath is null ? null
            : new RawBinaryExporter(args.RawBinPath);

        var sw = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        var readBuf = new byte[args.ReadSize];

        try
        {
            while (true)
            {
                int n = source.Read(readBuf);
                if (n == 0)
                {
                    if (args.Replay is not null) break; // EOF for file source
                    // serial source: timeout; just go around
                    continue;
                }

                foreach (var ev in framer.Feed(readBuf.AsMemory(0, n)))
                {
                    switch (ev)
                    {
                        case PacketReceivedEvent pr:
                        {
                            var p = pr.Packet;
                            drops.Observe(p.Header.Seq);
                            rate.Observe(p.Header.TimeUs, p.Header.Count, sw.Elapsed.TotalSeconds);
                            packets++;
                            samples += p.Header.Count;
                            for (int i = 0; i < p.Samples.Length; i++)
                            {
                                channelStats.Add(i % args.Channels, p.Samples[i]);
                            }
                            csv?.WritePacket(p, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                            if (rawBin is not null)
                            {
                                var bytes = PacketCodec.Encode(
                                    p.Header.Seq, p.Header.TimeUs, p.Samples,
                                    p.Header.Version, p.Header.Flags);
                                rawBin.WritePacketBytes(bytes);
                            }
                            break;
                        }
                        case BadCrcEvent: badCrc++; break;
                        case BadVersionEvent: badVersion++; break;
                        case ResyncDroppedEvent rd: resyncBytes += rd.Bytes; break;
                    }
                }

                if (!args.Quiet && (sw.Elapsed - lastProgress) >= TimeSpan.FromSeconds(1))
                {
                    PrintProgress(sw.Elapsed.TotalSeconds, packets, badCrc, drops.Drops, resyncBytes, rate);
                    lastProgress = sw.Elapsed;
                }

                if (args.DurationSec is double d && sw.Elapsed.TotalSeconds >= d) break;
            }

            var trailing = framer.FlushResync();
            if (trailing is not null) resyncBytes += trailing.Bytes;
        }
        finally
        {
            csv?.Dispose();
            rawBin?.Dispose();
            source.Dispose();
        }

        PrintSummary(sw.Elapsed.TotalSeconds, packets, badCrc, badVersion, drops.Drops,
            resyncBytes, samples, rate, channelStats);
        return 0;
    }

    private static void PrintProgress(double elapsed, long packets, long badCrc,
        long drops, long resyncBytes, RateEstimator rate)
    {
        double stm = rate.Stm32RateHz() ?? 0.0;
        double host = rate.HostRateHz() ?? 0.0;
        Console.Error.WriteLine(
            $"[{elapsed,6:F1}s] pkts={packets,6:D} bad_crc={badCrc,3:D} " +
            $"drops={drops,4:D} resync={resyncBytes,5:D}B " +
            $"stm32={stm,7:F1}Hz host={host,7:F1}Hz");
    }

    private static void PrintSummary(double elapsed, long packets, long badCrc, long badVersion,
        long drops, long resyncBytes, long samples, RateEstimator rate, ChannelStats cs)
    {
        Console.WriteLine();
        Console.WriteLine("Capture summary");
        Console.WriteLine("---------------");
        Console.WriteLine($"  elapsed                 : {elapsed:F2} s");
        Console.WriteLine($"  packets received        : {packets}");
        Console.WriteLine($"  bad CRC                 : {badCrc}");
        Console.WriteLine($"  bad version             : {badVersion}");
        Console.WriteLine($"  dropped packets         : {drops}");
        Console.WriteLine($"  resync bytes skipped    : {resyncBytes}");
        Console.WriteLine($"  samples received        : {samples}");
        var stm = rate.Stm32RateHz();
        var host = rate.HostRateHz();
        Console.WriteLine(stm is null
            ? "  STM32-derived rate      : (insufficient data)"
            : $"  STM32-derived rate      : {stm:F1} Hz");
        Console.WriteLine(host is null
            ? "  host-derived rate       : (insufficient data)"
            : $"  host-derived rate       : {host:F1} Hz");

        Console.WriteLine();
        Console.WriteLine("Per-channel statistics");
        Console.WriteLine("----------------------");
        for (int ch = 0; ch < cs.ChannelCount; ch++)
        {
            var s = cs.Get(ch);
            if (s.Count == 0)
            {
                Console.WriteLine($"  channel {ch}: no samples");
                continue;
            }
            Console.WriteLine(
                $"  channel {ch}: n={s.Count} min={s.Min:F1} max={s.Max:F1} " +
                $"mean={s.Mean:F2} std={s.Std:F3}");
        }
    }
}
