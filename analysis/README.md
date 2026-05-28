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

### Layout

The window is split into three persistent sections plus a tab strip:

1. **Persistent CONNECTION card** at the top. Port dropdown (live-populated from `SerialPort.GetPortNames()`), Refresh button, Connect / Disconnect toggle, validation-label text box, **Record / Stop** toggle. A status dot and short text show idle / connected / replaying / error states. Stays visible across all tabs because you usually want to see connection state regardless of what you are doing.

   **Recording**: with a live capture running, clicking Record writes the raw incoming byte stream to a timestamped `.bin` file under the samples directory (e.g. `samples/zener-20260528-153012.bin`). The filename uses the current validation label as a prefix (or `capture-` if no label is set). Stop recording either by clicking the Record button again or by disconnecting; the file flushes to disk and immediately appears in the Samples tab list (the samples list auto-refreshes on stop). Recorded files are the same on-wire format the firmware emits and the same format the Samples tab replays, so a capture you recorded yourself can be re-loaded and inspected later just like a synthetic sample.
2. **TabControl** in the middle, three tabs:
   - **Live capture** tab. Three stacked cards:
     - **WAVEFORM** card. Rolling-window waveform per channel with a channel-colour legend (channel 0 zener / channel 1 baseline). Y-axis ticks at 0 / 1024 / 2048 / 3072 / 4095 (12-bit). Mid-rail dashed reference line. 60 fps redraw.
     - **DISTRIBUTION** card. Cumulative per-channel histogram drawn as line envelopes with a low-alpha fill. X-axis ticks at the same 12-bit codes. 10 Hz redraw.
     - **PER-CHANNEL STATISTICS** card. Monospaced row per channel showing n, min, max, mean, std.
   - **Samples** tab. ListBox of every `*.bin` file in the samples directory. Each row shows file name, file size (KB / MB), and "modified" age ("3 min ago", "2 d ago"). Header shows the resolved samples directory path. Click any row and the GUI:
     - Stops any active live capture
     - Resets all stats, the waveform ring buffer, and the histogram
     - Opens the file through `TinyChaos.Protocol.Framer` and replays it
     - Switches the status footer's mode pill to `replay`
     Refresh re-enumerates the directory. Open folder opens it in Finder / Explorer / xdg-open.
   - **Firmware** tab. **BUILD & FLASH** card. Self-test, Build, and Flash buttons that shell out to `make test`, `make`, and `make flash` respectively in the firmware directory. A `Clear` button resets the streaming console. The console is a full-tab scrollable monospaced text panel that captures stdout and stderr from the subprocess line by line; stderr lines are prefixed with `! ` for visual distinction. A status indicator next to the title shows idle / building / flashing / ok / failed. Buttons disable while a subprocess is running so you cannot double-fire.

     Firmware directory discovery: same algorithm as samples (`TINYCHAOS_FIRMWARE` env var, walk up looking for `firmware/` next to `.git`, or display "not found" if neither resolves).
3. **Persistent status footer** at the bottom. Lists mode (live / replay), packets, bad CRC count, dropped packets, resync bytes, the STM32-derived sample rate, the host-derived sample rate, and the active validation label as tabular monospaced pill badges. Stays visible across all tabs.

### Samples directory discovery

The GUI looks for samples in this order:

1. The `TINYCHAOS_SAMPLES` environment variable, if it points at an existing directory.
2. Walk up from the executable's `AppContext.BaseDirectory` looking for a `samples/` folder next to a `.git` entry. This finds `<repo-root>/samples/` during development and from any checkout.
3. Fall back to `~/tinychaos-samples/`, created on first run.

To override at launch:

```
TINYCHAOS_SAMPLES=/path/to/my/captures dotnet run --project src/TinyChaos.Gui -c Release
```

### Threading and refresh

Both views are custom Avalonia `Control` subclasses that override `Render`. They poll their backing models on a 60 Hz dispatcher timer for the waveform and 10 Hz for the histogram, decoupling render rate from packet rate.

The capture service reads bytes on a background `Task.Run` thread, feeds them through `TinyChaos.Protocol.Framer`, and appends decoded samples into thread-safe `WaveformModel` and `HistogramModel` instances under a per-model lock. The view-model has a separate 10 Hz dispatcher timer that re-formats the status-pill strings from `CaptureService.Snapshot()`.

This producer/consumer split means the UI stays smooth at 60 fps even when the firmware is pumping 100 kHz × 2 channels of samples; the renderer just takes a fresh ring-buffer snapshot on its own clock.

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
- CSV export from the GUI. The CLI is the canonical CSV producer; the GUI is for live monitoring and replay inspection. Adding CSV export is straightforward: instantiate `TinyChaos.Host.Export.CsvExporter` in `CaptureService` and call `WritePacket` on each `PacketReceivedEvent`.

These are deliberate scope cuts to keep the v1 .NET surface small and reliable. Add them when you need them; the protocol and stats layers will not need to change.
