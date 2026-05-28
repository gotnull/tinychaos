# tinychaos

A hardware random number generator that captures avalanche-breakdown noise from a reverse-biased zener diode, digitises it on an STM32 NUCLEO-H753ZI, streams framed binary packets to a host, and analyses them. Two host implementations ship in this repo, sharing the same wire protocol byte-for-byte: a Python toolchain (preferred on macOS / Linux) and a .NET 8 C# toolchain (preferred on Windows, also runs cross-platform).

Every stage of the pipeline is observable. There is no manual hex editing, no ASCII UART dump format, no opaque transport. The host parser detects CRC failures, counts dropped packets, recovers from corruption, and reports two independent sample-rate estimates.

## Status

| Stage                                      | State                                              |
|--------------------------------------------|----------------------------------------------------|
| Bill of materials (element14 Australia)    | Done. See [BOM.md](BOM.md).                        |
| Design docs                                | Done. See [docs/](docs/).                          |
| Authoritative pipeline doc                 | Done. See [docs/ENTROPY_CAPTURE_PIPELINE.md](docs/ENTROPY_CAPTURE_PIPELINE.md). |
| Host Python package and CLI                | Done. See [tools/](tools/).                        |
| Host Python test suite (68 tests, pytest)  | Passing.                                           |
| Host C# .NET 8 solution and CLI            | Done. See [analysis/](analysis/).                  |
| Host C# Avalonia GUI (cross-platform)      | Done. Live waveform (60 fps), cumulative histogram, per-channel statistics, samples-replay panel that lists `samples/*.bin` and plays them back on click, and a BUILD & FLASH panel that shells out to the firmware Makefile (Self-test, Build, Flash) with a live streaming console. macOS, Windows, Linux from one codebase. See [analysis/README.md](analysis/README.md). |
| Tracked sample captures                    | Three synthetic `.bin` files under [samples/](samples/) so the GUI and CLI replay paths can be exercised without hardware. See [samples/README.md](samples/README.md). |
| Host C# test suite (xUnit)                 | Code written; tests run by anyone with the .NET 8 SDK via `dotnet test`. |
| Firmware portable protocol module (C)      | Done. Verified byte-identical to Python and C# references. |
| Firmware on-host self-test                 | Passing. `make -C firmware test`.                  |
| Firmware transport modules (USB CDC, UART) | Implemented. Awaiting CubeMX project generation.   |
| Firmware ADC and DMA capture               | Skeleton present in `main_skeleton.c`; full integration after CubeMX. |
| Live plotting (matplotlib, Python only)    | Done. `tinychaos.plotting`, optional `plot` extra. CLI degrades cleanly if absent. |
| Offline analysis (rolling, Z-score, FFT)   | Done in Python (`tinychaos.analysis`). C# v1 is CLI-only; capture once with either host and analyse later. |

## What you can do right now (no hardware required)

The host parser, CRC, framer, stats, exporters, and CLI all work against synthetic data. So does the firmware protocol module via the on-host self-test. You can verify everything in this repo runs cleanly before sourcing a single component.

The next section walks through every command, in order.

---

## Step-by-step setup and verification

The instructions below cover both host implementations. The Python steps were tested on macOS; the C# steps work on Windows, macOS, and Linux. If you only care about one host, skip the steps for the other.

### 0. Clone

```
git clone <your remote here> tinychaos
cd tinychaos
```

If this is a fresh repo with no remote yet, just `cd` into the directory.

### 1. Confirm prerequisites for the host stack

Check the Python version:

```
python3 --version          # expect 3.10 or newer
```

Install Python 3.10+ if needed:

- macOS: `brew install python`
- Windows: `winget install Python.Python.3.12` or download from [python.org](https://www.python.org/downloads/windows/) (tick "Add to PATH" in the installer).
- Linux (Debian/Ubuntu): `sudo apt install python3 python3-venv python3-pip`.

### 2. Create the Python virtual environment and install the host tools

**macOS / Linux**:

```
cd tools
python3 -m venv .venv
source .venv/bin/activate
pip install -U pip
pip install -e .[dev]
```

**Windows (PowerShell)**:

```
cd tools
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -U pip
pip install -e .[dev]
```

(Use `.venv\Scripts\activate.bat` from cmd.exe instead of PowerShell.)

Optional extras for live plotting (any OS):

```
pip install -e .[dev,plot]
```

### 3. Run the host test suite

```
pytest
```

Expected result: **54 passed** in under a second. The suite covers:

- CRC-16/CCITT-FALSE known-answers and parametrised vectors.
- Protocol encode and decode round-trips for 0, 1, 8, 256, and 1024 samples.
- Explicit byte-layout assertions matching the docs.
- Rejection of bad magic, bad CRC, wrong version, truncated, and short buffers.
- Encode-side validation of integer ranges.
- Streaming framer recovery from leading garbage, mid-packet bit flips, flipped magic, truncation, and sequence gaps.
- Hypothesis-driven chunk-size invariance: same byte stream in one-byte chunks, fixed chunks, or random chunks yields the same event sequence.
- Hypothesis-driven single-bit-flip safety: no bit flip causes a silent corrupted PacketReceived.
- Rolling stats matched against numpy ground truth.
- Drop tracker with uint32 wraparound.
- Rate estimator at constant rate and across a `time_us` wraparound boundary.
- CLI smoke test via `--replay` against a generated binary file.

### 4. Smoke-test the CLI end-to-end against synthetic data

Build a synthetic binary capture and replay it through the CLI:

```
python -c "
from tinychaos.protocol import encode_packet
with open('/tmp/tinychaos-demo.bin','wb') as f:
    for i in range(20):
        samples = [(i*8 + n) & 0xFFF for n in range(8)]
        f.write(encode_packet(seq=i, time_us=i*25600, samples=samples))
"
python -m tinychaos.cli --replay /tmp/tinychaos-demo.bin --csv /tmp/tinychaos-demo.csv --validation-label demo --quiet
head -5 /tmp/tinychaos-demo.csv
```

Expected: a summary line showing 20 packets, 0 bad CRC, 0 drops, 160 samples, and a CSV with header `host_time,packet_seq,stm32_time_us,sample_index,channel_index,adc_value,validation_label`.

### 4a. (Alternative or in addition) C# host setup

If you are on Windows, or you prefer .NET, you can use the C# host instead of (or alongside) the Python one. It speaks the same wire protocol byte-for-byte.

Prerequisites:

- Windows: install via `winget install Microsoft.DotNet.SDK.8` or from [dot.net](https://dot.net/).
- macOS: `brew install --cask dotnet-sdk`.
- Linux: follow distro instructions at [learn.microsoft.com/dotnet](https://learn.microsoft.com/dotnet/core/install/linux).

Confirm: `dotnet --version` should print 8.0 or newer. If you have a different major (e.g. .NET 10), the solution rolls forward automatically via `analysis/Directory.Build.props`.

Build, test, and run all three .NET artefacts:

```
cd analysis
dotnet restore
dotnet build -c Release
dotnet test -c Release
```

Run the **CLI** against an existing capture (replay mode, no hardware needed):

```
dotnet run --project src/TinyChaos.Host -c Release -- --replay ../samples/zener_synthetic.bin --csv /tmp/zener.csv --quiet
```

Run the **Avalonia GUI** (cross-platform: macOS / Windows / Linux):

```
dotnet run --project src/TinyChaos.Gui -c Release
```

What you should see in the GUI:

- **CONNECTION** card with a port dropdown, validation-label text box, Refresh, and Connect.
- **SAMPLES** card listing every `*.bin` file under [samples/](samples/). Click a row and the GUI immediately decodes it through the framer and renders the result.
- **WAVEFORM** card with the per-channel live trace, 60 fps redraw, 12-bit Y-axis ticks, channel-colour legend.
- **DISTRIBUTION** card with the cumulative per-channel histogram.
- **PER-CHANNEL STATISTICS** card with n / min / max / mean / std per channel.
- **Status footer** with mode (live/replay), packets, bad CRC, drops, resync bytes, STM32-derived rate, host-derived rate, and validation label.

The samples directory is resolved by walking up from the executable looking for a `samples/` folder next to a `.git` entry. You can override with the `TINYCHAOS_SAMPLES` environment variable.

See [analysis/README.md](analysis/README.md) for project layout, live-capture flags, the cross-implementation parity test, and the standalone-app-bundle publish commands.

### 5. Run the firmware on-host self-test

This step does NOT need any STM32 toolchain. It compiles the firmware's portable C protocol module with the system gcc/clang and verifies its CRC and byte-layout match the Python reference.

```
cd ../firmware
make test
```

Expected output ends with:

```
encoded packet [24 bytes]:
  DA 7A 01 00 44 33 22 11 88 77 66 55 04 00 02 01
  04 03 06 05 08 07 92 4D
all checks passed
```

### 6. Verify byte-for-byte parity between firmware C and host Python

The host test `tools/tests/test_protocol.py::test_explicit_byte_layout` already covers the host-side bytes. To cross-check the C output against Python explicitly:

```
cd ../tools
source .venv/bin/activate
python -c "
from tinychaos.protocol import encode_packet
b = encode_packet(seq=0x11223344, time_us=0x55667788, samples=[0x0102, 0x0304, 0x0506, 0x0708])
print(b.hex().upper())
"
```

Expected: `DA7A0100443322118877665504000201040306050807924D` (same 24 bytes that the firmware test printed in step 5).

### 7. Source the hardware

See [BOM.md](BOM.md). The BOM is sourced from element14 Australia and the entire list can be added to your cart in one upload via [hardware/element14-bom.csv](hardware/element14-bom.csv) at [au.element14.com/bom-tool](https://au.element14.com/bom-tool).

The STM32 NUCLEO-H753ZI is not in the CSV: you already have one.

### 8. Build the analogue front-end on a breadboard

Wire up the chain described in [docs/hardware-design.md](docs/hardware-design.md):

- 1N4735 zener (or 4.7/5.1/5.6 V variants for the validation comparison runs).
- 100 kohm multiturn cermet trimpot in series with the zener for bias.
- 1 uF MKT film AC coupling cap.
- Two-stage low-noise amplifier (NE5534 then LM833) with mid-rail biasing.
- ADC protection: 1 kohm series + two BAT46 Schottky clamps + 100 nF anti-alias on each ADC input. See [docs/adc-protection.md](docs/adc-protection.md).
- Decoupling: 100 nF MLCC + 10 uF electrolytic + ferrite bead at every op-amp Vcc pin.
- A second ADC input wired permanently to a mid-rail 2 x 100 kohm divider as the baseline channel.

Power the zener from 2 x 9 V batteries in series for the cleanest measurement. Power the amplifier rail and the NUCLEO from the same USB cable that hosts the data link.

Safety: the STM32 ADC input must never exceed 3.3 V. The clamp network is what guarantees this even if the amplifier saturates. Do NOT skip it.

### 9. Generate the STM32CubeMX project and build the firmware

Detailed steps and the exact CubeMX peripheral configuration are in [firmware/README.md](firmware/README.md). Summary:

1. Install: `brew install --cask stm32cubemx`, `brew install arm-none-eabi-gcc stlink`.
2. Open CubeMX, create a project for NUCLEO-H753ZI.
3. Configure the clock tree (480 MHz core), ADC1 channels IN0 + IN3, TIM2 as the ADC trigger at 10 kHz, TIM5 as a free-running 1 MHz counter for `time_us`, USB OTG FS in CDC mode, USART3 at 921600 baud as the fallback transport.
4. Project Manager: Toolchain = Makefile. Project location = parent of this `firmware/` folder. Generate.
5. Move the generated Makefile aside: `mv firmware/Makefile firmware/Makefile.cube`. The top-level `firmware/Makefile` picks it up automatically.
6. Apply the integration edits documented in `firmware/Core/Src/main_skeleton.c`. Specifically, paste the documented snippets into the CubeMX `USER CODE BEGIN/END` markers in `main.c` and into the USB and UART completion callbacks. Do not replace the whole files.
7. Pick the transport. Uncomment ONE of `#define ENTROPY_TRANSPORT_USB` or `#define ENTROPY_TRANSPORT_UART` in `firmware/Core/Inc/entropy_config.h`.
8. Build: `cd firmware && make`. The ELF, HEX, and BIN are in `build/`.
9. Flash: `make flash`, or directly `st-flash write build/tinychaos.bin 0x08000000`.

### 10. Verify the firmware-to-host link with the counter pattern (no ADC yet)

`main_skeleton.c` ships with `ENTROPY_USE_COUNTER_PATTERN = 1`. The firmware emits packets whose samples are a 12-bit counter rather than real ADC data. This isolates protocol and transport from analogue questions.

#### Finding the serial port name

The same NUCLEO appears under different names depending on the host OS. All examples below use a placeholder; substitute the real port path before you run.

- **macOS**: `ls /dev/tty.usbmodem*` (typical: `/dev/tty.usbmodemXXXXX`).
- **Linux**: `ls /dev/ttyACM* /dev/ttyUSB*` (typical: `/dev/ttyACM0`).
- **Windows**: open Device Manager and look under "Ports (COM & LPT)" for "STMicroelectronics STLink Virtual COM Port", or run `Get-CimInstance Win32_SerialPort | Select-Object DeviceID, Description` in PowerShell. Typical names: `COM3`, `COM4`, etc. Pass the bare name without the `\\.\` prefix (the Python and C# tools handle it).

#### Run the live-capture smoke test

With the board plugged in, from `tools/` with the virtual environment active:

```
# macOS / Linux
python -m tinychaos.cli --port /dev/tty.usbmodemXXXXX --duration 30 --csv /tmp/counter.csv

# Windows (PowerShell)
python -m tinychaos.cli --port COM4 --duration 30 --csv $env:TEMP\counter.csv
```

Expected:

- Packets received: in the low tens of thousands (depending on packet rate).
- Bad CRC: 0.
- Dropped packets: 0.
- STM32-derived rate: matches `ADC_SAMPLE_RATE_HZ`.
- Host-derived rate: within a few hundred ppm of the STM32-derived rate.
- CSV `adc_value` column shows a clean 12-bit counter modulo 4096.

If those four properties hold, the protocol, framer, transport, and clocks are all correct. Move on to ADC.

### 11. Switch to real ADC capture

In `main_skeleton.c` (or wherever you applied its content in your CubeMX `main.c`), set:

```
#define ENTROPY_USE_COUNTER_PATTERN 0
```

Rebuild, flash, and capture again. The CSV will now contain ADC samples instead of the counter. The two channels alternate in `channel_index` 0 and 1 as documented in [docs/ENTROPY_CAPTURE_PIPELINE.md](docs/ENTROPY_CAPTURE_PIPELINE.md) section 9.

### 12. Run the validation comparison set

Five short captures, each with a different physical wiring, isolate different noise sources. Each gets its own CSV file labelled via `--validation-label`.

Substitute `<PORT>` with your real serial-port name (see step 10).

```
mkdir -p captures

# A. Shorted input. Wire the ADC input directly to the mid-rail divider tap.
python -m tinychaos.cli --port <PORT> --duration 60 \
    --csv captures/shorted.csv --validation-label shorted

# B. Baseline divider. The default wiring of the dedicated reference channel.
python -m tinychaos.cli --port <PORT> --duration 60 \
    --csv captures/divider.csv --validation-label divider

# C. Floating input. Leave the zener channel's ADC wire unattached.
python -m tinychaos.cli --port <PORT> --duration 60 \
    --csv captures/floating.csv --validation-label floating

# D. Zener, wall PSU.
python -m tinychaos.cli --port <PORT> --duration 60 \
    --csv captures/zener_psu.csv --validation-label zener

# E. Zener, battery bias.
python -m tinychaos.cli --port <PORT> --duration 60 \
    --csv captures/zener_battery.csv --validation-label battery
```

Expected signatures and what each isolates are described in [docs/ENTROPY_CAPTURE_PIPELINE.md](docs/ENTROPY_CAPTURE_PIPELINE.md) section 14.

### 13. (Optional) Live plotting

If you installed the `plot` extra:

```
python -m tinychaos.cli --port <PORT> --plot
```

A matplotlib window opens with three panels: rolling waveform per channel, cumulative histogram per channel, and a stats text panel. If matplotlib is not installed the CLI prints a friendly diagnostic and continues without plotting.

### 14. (Optional) Offline FFT

```
python -m tinychaos.cli --port <PORT> --duration 30 --fft --csv ./zener.csv
```

The CLI keeps up to 65 536 samples per channel in memory during capture. At exit it computes a one-sided PSD via `tinychaos.analysis.fft_psd`, prints the peak frequency and PSD value per channel, and, if matplotlib is installed, opens a plot. Verified end-to-end against a known 1 kHz sine: the CLI reports the peak within one FFT bin.

---

## Repository layout

```
tinychaos/
  README.md                       this file
  BOM.md                          element14 bill of materials
  hardware/
    element14-bom.csv             upload to element14 BOM tool to populate the cart
  docs/
    ENTROPY_CAPTURE_PIPELINE.md   authoritative pipeline reference
    architecture.md               two-zone breadboard layout, design claims
    hardware-design.md            schematic, values, bias calcs
    filtering-and-power.md        RC corners, decoupling, battery isolation
    adc-protection.md             Schottky clamp network and layout
    firmware-notes.md             superseded redirect to ENTROPY_CAPTURE_PIPELINE
    analysis-app.md               superseded redirect to ENTROPY_CAPTURE_PIPELINE
  tools/                          host Python package
    pyproject.toml
    README.md                     tools-local quickstart
    src/tinychaos/                package source
    tests/                        pytest suite (68 tests)
  firmware/                       STM32 firmware
    Makefile                      host self-test plus CubeMX-Makefile passthrough
    README.md                     CubeMX generation and integration steps
    Core/Inc/                     entropy_config.h, entropy_protocol.h, usb_stream.h, serial_stream.h
    Core/Src/                     entropy_protocol.c, usb_stream.c, serial_stream.c, main_skeleton.c
    test/                         test_protocol_host.c
  analysis/                       .NET 8 host: Protocol library, CLI (tinychaos-host), Avalonia GUI (tinychaos-gui), xUnit tests
    TinyChaos.sln
    Directory.Build.props         RollForward=LatestMajor so apps run on .NET 9 or 10 when 8 runtime is absent
    src/TinyChaos.Protocol/       wire-format library, shared by CLI and GUI
    src/TinyChaos.Host/           console CLI
    src/TinyChaos.Gui/            Avalonia desktop GUI: connection card, samples card, waveform, histogram, stats
    tests/TinyChaos.Tests/        xUnit suite (38 tests)
  samples/                        tracked synthetic .bin captures for replay testing
    README.md                     description of each sample
    zener_synthetic.bin           Gaussian noise mimicking real avalanche noise
    sine_1khz.bin                 1 kHz sine sanity-check signal
    floating_50hz.bin             50 Hz mains-pickup simulation
```

## Quick reference: every command, in one place

```
# Python host stack
cd tools && python3 -m venv .venv && source .venv/bin/activate
pip install -e .[dev,plot]
pytest

# C# host stack
cd ../analysis
dotnet restore && dotnet build -c Release
dotnet test -c Release

# C# Avalonia GUI (cross-platform: macOS, Windows, Linux)
dotnet run --project src/TinyChaos.Gui -c Release

# Firmware on-host self-test
cd ../firmware
make test

# Cross-implementation byte parity
cd ../tools && source .venv/bin/activate
python -c "from tinychaos.protocol import encode_packet; print(encode_packet(0x11223344, 0x55667788, [0x0102,0x0304,0x0506,0x0708]).hex().upper())"
# expected: DA7A0100443322118877665504000201040306050807924D

# Synthetic CLI smoke
python -c "from tinychaos.protocol import encode_packet; open('/tmp/x.bin','wb').write(b''.join(encode_packet(i,i*25600,[(i*8+n)&0xFFF for n in range(8)]) for i in range(20)))"
python -m tinychaos.cli --replay /tmp/x.bin --csv /tmp/x.csv --quiet

# Real hardware capture (once firmware is flashed)
#   Substitute <PORT>:  COM4 (Windows), /dev/tty.usbmodemXXXXX (macOS), /dev/ttyACM0 (Linux)
python -m tinychaos.cli --port <PORT> --duration 30 --csv ./run.csv

# Validation run with a label
python -m tinychaos.cli --port <PORT> --duration 60 --csv captures/zener.csv --validation-label zener
```

## Safety, in one paragraph

The STM32 ADC absolute-maximum input is 3.3 V + 0.3 V. The amplifier output can saturate to its own supply rail at any time. The ADC clamp network (1 kohm series + two BAT46 Schottky + 100 nF) is mandatory and is what stands between the amplifier and the pin. Never connect the zener chain output directly to a NUCLEO ADC pin. See [docs/adc-protection.md](docs/adc-protection.md) for the layout and rationale. Bias the zener from batteries when you want the cleanest measurement; mains-derived noise on the bias rail is the most common source of "what is that 50 Hz peak in my supposedly-white-noise capture".

## Where to look next

- For protocol questions: [docs/ENTROPY_CAPTURE_PIPELINE.md](docs/ENTROPY_CAPTURE_PIPELINE.md).
- For schematic-level questions: [docs/hardware-design.md](docs/hardware-design.md).
- For firmware integration questions: [firmware/README.md](firmware/README.md).
- For host tool API questions: read the docstrings in `tools/src/tinychaos/*.py`. The modules are small and intentionally so.
