namespace TinyChaos.Host;

/// <summary>Parsed command-line arguments for the tinychaos host CLI.</summary>
public sealed class CliArgs
{
    public string? Port { get; set; }
    public string? Replay { get; set; }
    public int Baud { get; set; } = 921600;
    public string? CsvPath { get; set; }
    public string? RawBinPath { get; set; }
    public string ValidationLabel { get; set; } = "";
    public double? DurationSec { get; set; }
    public int Channels { get; set; } = 2;
    public bool Quiet { get; set; }
    public int ReadSize { get; set; } = 4096;

    public static CliArgs Parse(string[] argv)
    {
        var a = new CliArgs();
        for (int i = 0; i < argv.Length; i++)
        {
            string arg = argv[i];
            string? next = i + 1 < argv.Length ? argv[i + 1] : null;
            switch (arg)
            {
                case "--port":
                    a.Port = Require(next, arg); i++; break;
                case "--replay":
                    a.Replay = Require(next, arg); i++; break;
                case "--baud":
                    a.Baud = int.Parse(Require(next, arg)); i++; break;
                case "--csv":
                    a.CsvPath = Require(next, arg); i++; break;
                case "--raw-bin":
                    a.RawBinPath = Require(next, arg); i++; break;
                case "--validation-label":
                    a.ValidationLabel = Require(next, arg); i++; break;
                case "--duration":
                    a.DurationSec = double.Parse(Require(next, arg)); i++; break;
                case "--channels":
                    a.Channels = int.Parse(Require(next, arg)); i++; break;
                case "--quiet":
                    a.Quiet = true; break;
                case "--read-size":
                    a.ReadSize = int.Parse(Require(next, arg)); i++; break;
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"unknown argument: {arg}");
            }
        }

        if (a.Port is null == a.Replay is null)
        {
            throw new ArgumentException("exactly one of --port or --replay is required");
        }
        if (a.Channels < 1)
        {
            throw new ArgumentException("--channels must be >= 1");
        }
        return a;
    }

    private static string Require(string? value, string flag)
    {
        if (value is null) throw new ArgumentException($"{flag} requires a value");
        return value;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
tinychaos-host: read framed binary packets from the tinychaos firmware,
validate CRCs, track drops, and export to CSV or raw binary.

Usage:
  tinychaos-host --port <port> [options]
  tinychaos-host --replay <file> [options]

Source (exactly one required):
  --port <name>             Serial port, e.g. COM3 on Windows or
                            /dev/tty.usbmodemXXXX on macOS / Linux.
  --replay <file>           Replay a captured raw binary file.

Options:
  --baud <int>              Baud rate (default 921600). Ignored over USB CDC.
  --csv <path>              Write decoded samples to this CSV.
  --raw-bin <path>          Append each accepted packet to this binary file.
  --validation-label <s>    String written to the validation_label CSV column.
  --duration <sec>          Stop after this many seconds of host wall-clock.
  --channels <int>          Interleaved channel count per packet (default 2).
  --quiet                   Suppress periodic per-second progress lines.
  --read-size <int>         Bytes per read from the source (default 4096).
  --help                    Print this help.
""");
    }
}
