# TinyChaos host (C#)

.NET 8 host-side tooling for the tinychaos entropy capture pipeline. Mirrors the Python implementation in [`../tools/`](../tools/) function-for-function and shares the same wire protocol byte-for-byte. Runs on Windows, macOS, and Linux.

Two front-ends ship in this solution:

- **`TinyChaos.Host`** is the console CLI. Same flags as the Python CLI, no graphics.
- **`TinyChaos.Gui`** is an Avalonia desktop GUI. Live waveform, cumulative histogram, per-channel stats panel, port picker, validation label. Renders identically on macOS, Windows, and Linux from a single codebase.

Use the CLI for scripted captures and CI. Use the GUI for interactive exploration.

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
    TinyChaos.Gui/                 Avalonia desktop GUI, depends on Protocol and Host (for shared stats)
      Program.cs
      App.axaml + App.axaml.cs
      MainWindow.axaml + MainWindow.axaml.cs
      MainWindowViewModel.cs       MVVM with CommunityToolkit.Mvvm
      WaveformModel.cs             per-channel ring buffer
      HistogramModel.cs            per-channel cumulative histogram
      CaptureService.cs            background serial reader, feeds the Framer
      WaveformView.cs              custom Avalonia Control with DrawingContext Render
      HistogramView.cs             custom Avalonia Control with DrawingContext Render
      app.manifest
      TinyChaos.Gui.csproj
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

## Running the Avalonia GUI

```
cd analysis
dotnet run --project src/TinyChaos.Gui -c Release
```

The window opens with a Port dropdown (populated from `SerialPort.GetPortNames()`), a Refresh button, a Connect / Disconnect toggle, and a Label text box (written into the validation_label CSV column if CSV export is added later). Three stacked panels show the live waveform per channel (top, 2/3 of the height), the cumulative histogram (middle), and the per-channel statistics (bottom). A status bar runs along the bottom of the window with packets, bad CRC, drops, resync bytes, and the dual sample-rate estimate.

Both views are custom Avalonia Controls that override `Render`. They poll their backing models on a 30 Hz (waveform) and 10 Hz (histogram) dispatcher timer, decoupling render rate from packet rate. The capture service reads bytes on a background `Task.Run` thread, feeds them through `TinyChaos.Protocol.Framer`, and appends decoded samples into thread-safe `WaveformModel` and `HistogramModel` instances under a per-model lock.

### Publishing standalone app bundles

```
# macOS Apple Silicon
dotnet publish src/TinyChaos.Gui -c Release -r osx-arm64 --self-contained
# macOS Intel
dotnet publish src/TinyChaos.Gui -c Release -r osx-x64 --self-contained
# Windows x64
dotnet publish src/TinyChaos.Gui -c Release -r win-x64 --self-contained
# Linux x64
dotnet publish src/TinyChaos.Gui -c Release -r linux-x64 --self-contained
```

The resulting `tinychaos-gui` binary is in `src/TinyChaos.Gui/bin/Release/net8.0/<rid>/publish/`. Self-contained builds bundle the .NET runtime so no .NET install is required on the target machine.

## Out of scope for now

- FFT and Z-score analysis in the C# host. The Python `tinychaos.analysis` module covers this and produces CSVs that any tool (including the C# host) can read.
- CSV export from the GUI. The CLI is the canonical CSV producer; the GUI is for live monitoring. Adding CSV export is straightforward: instantiate `TinyChaos.Host.Export.CsvExporter` in `CaptureService` and call `WritePacket` on each `PacketReceivedEvent`.
- Replay-from-file mode in the GUI. Use the CLI with `--replay` for that.

These are deliberate scope cuts to keep the v1 .NET surface small and reliable. Add them when you need them; the protocol and stats layers will not need to change.
