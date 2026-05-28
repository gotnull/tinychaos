# TinyChaos host (C#)

.NET 8 host-side tooling for the tinychaos entropy capture pipeline. Mirrors the Python implementation in [`../tools/`](../tools/) function-for-function and shares the same wire protocol byte-for-byte. Runs on Windows, macOS, and Linux.

Use this host when you prefer .NET tooling or when you are running on Windows. Use [`../tools/`](../tools/) (Python) when you prefer scientific-Python tooling on macOS or Linux.

## Layout

```
analysis/
  TinyChaos.sln                    .NET solution
  src/
    TinyChaos.Protocol/            wire-format library (CRC, codec, framer)
      Crc16.cs
      Packet.cs
      Exceptions.cs
      PacketCodec.cs
      FrameEvent.cs
      Framer.cs
      TinyChaos.Protocol.csproj
    TinyChaos.Host/                CLI executable, depends on Protocol
      Program.cs
      CliArgs.cs
      Stats/RollingStats.cs
      Stats/DropTracker.cs
      Stats/RateEstimator.cs
      Stats/ChannelStats.cs
      Io/IByteSource.cs
      Io/FileSource.cs
      Io/SerialSource.cs
      Export/CsvExporter.cs
      Export/RawBinaryExporter.cs
      TinyChaos.Host.csproj
  tests/
    TinyChaos.Tests/               xUnit test project
      Crc16Tests.cs
      PacketTests.cs
      FramerTests.cs
      StatsTests.cs
      TinyChaos.Tests.csproj
  README.md                        this file
```

## Prerequisites

- .NET 8 SDK or newer
  - Windows: download from [dot.net](https://dot.net/), or install via `winget install Microsoft.DotNet.SDK.8`
  - macOS: `brew install --cask dotnet-sdk`
  - Linux: follow distro instructions at [learn.microsoft.com/dotnet](https://learn.microsoft.com/dotnet/core/install/linux)

To verify: `dotnet --version` should print `8.x.y` or newer.

## Build

From the repo root:

```
cd analysis
dotnet restore
dotnet build -c Release
```

## Run the test suite

```
dotnet test -c Release
```

Expected: every test passes. The suite covers CRC known-answers, packet encode/decode round-trips for various sample counts, byte-level wire-format assertions (including the cross-implementation parity vector
`DA7A0100443322118877665504000201040306050807924D`), framer resync behaviour under leading garbage, bit-flips, version corruption, sequence gaps, and chunk-size invariance.

## Run the host CLI

After building, the binary lives at `src/TinyChaos.Host/bin/Release/net8.0/tinychaos-host.exe` on Windows or `tinychaos-host` on macOS / Linux. The simplest way to run it during development is via `dotnet run`:

```
cd src/TinyChaos.Host
dotnet run -c Release -- --help
```

End-to-end smoke against a synthetic capture (no hardware required), driven from the Python side and replayed in C#:

```
# Generate a synthetic capture using the Python encoder
cd ../../../tools
source .venv/bin/activate
python -c "from tinychaos.protocol import encode_packet; \
  open('/tmp/x.bin','wb').write(b''.join( \
    encode_packet(i, i*25600, [(i*8+n)&0xFFF for n in range(8)]) \
    for i in range(20)))"

# Decode it with the C# host
cd ../analysis
dotnet run --project src/TinyChaos.Host -c Release -- \
    --replay /tmp/x.bin --csv /tmp/x-cs.csv --quiet
```

Expected: a "Capture summary" section showing 20 packets, 0 bad CRC, 0 drops, with the same byte stream parsed identically to the Python CLI.

## Live capture from the board

Once the firmware is flashed and the NUCLEO is on a USB port:

- Windows: pick the COM port from Device Manager, then
  `dotnet run --project src/TinyChaos.Host -c Release -- --port COM4 --csv out.csv`
- macOS: `--port /dev/tty.usbmodemXXXX`
- Linux: `--port /dev/ttyACM0`

The CLI flags are the same as the Python tool: `--csv`, `--raw-bin`, `--validation-label`, `--duration`, `--channels`, `--quiet`, `--read-size`, `--help`.

## Wire-format authority

The protocol library is byte-compatible with both:

- The Python reference: `tools/src/tinychaos/protocol.py`
- The firmware: `firmware/Core/Src/entropy_protocol.c`

If you change the wire format, change all three. The cross-implementation parity test
`PacketTests.ParityWithPythonAndFirmware_FixedInput` will catch any divergence on the C# side.

For the full protocol spec see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8.

## Out of scope for now

- Live GUI plotting. The Python host has a matplotlib live-plot panel; the C# host is CLI only for v1. A WPF or Avalonia GUI sits behind this and can be added later. The protocol library is GUI-agnostic, so the work is purely a presentation layer.
- FFT and Z-score analysis in the C# host. The Python `tinychaos.analysis` module covers this and produces CSVs that any tool (including the C# host) can read.

These are deliberate scope cuts to keep the v1 .NET surface small and reliable. Add them when you need them; the protocol and stats layers will not need to change.
