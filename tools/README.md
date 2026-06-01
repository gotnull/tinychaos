# tinychaos tools

Host-side Python tooling for the tinychaos zener-noise entropy capture pipeline. Reads framed binary packets from the STM32 firmware over USB CDC (or UART), validates CRCs, recovers from corruption, exports CSV and raw binary, and optionally plots live.

Target platform: macOS. Should also work on Linux. Windows untested.

## Setup

**macOS / Linux**:

```
cd tools
python3 -m venv .venv
source .venv/bin/activate
pip install -e .[dev]
```

**Windows (PowerShell)**:

```
cd tools
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -e .[dev]
```

Add the `plot` extra if you want live plotting (any OS):

```
pip install -e .[dev,plot]
```

## Run

The same NUCLEO appears under different names per OS. Find your port name first:

- **Windows**: Device Manager > Ports (COM & LPT) > "STMicroelectronics STLink Virtual COM Port" (typical: `COM3`, `COM4`, ...). Or PowerShell: `Get-CimInstance Win32_SerialPort | Select DeviceID,Description`.
- **macOS**: `ls /dev/tty.usbmodem*` (typical: `/dev/tty.usbmodemXXXXX`).
- **Linux**: `ls /dev/ttyACM* /dev/ttyUSB*` (typical: `/dev/ttyACM0`).

Live capture from the board over USB CDC (substitute the real port):

```
# macOS
python -m tinychaos.cli --port /dev/tty.usbmodemXXXXX --csv out.csv
# Linux
python -m tinychaos.cli --port /dev/ttyACM0 --csv out.csv
# Windows
python -m tinychaos.cli --port COM4 --csv out.csv
```

Capture with a validation label and a duration cap:

```
python -m tinychaos.cli --port <PORT> --duration 60 --csv labels/zener.csv --validation-label zener
```

Replay a previously captured raw binary file (no hardware needed):

```
python -m tinychaos.cli --replay ../samples/zener_synthetic.bin --csv replay.csv
```

The `--baud` flag exists for compatibility with the UART fallback. Over USB CDC the baud rate is ignored by the underlying transport.

## Tests

```
pytest
```

## Layout

```
tools/
  pyproject.toml
  README.md
  src/tinychaos/
    __init__.py
    protocol.py        wire format, CRC, encode and decode
    framer.py          streaming resync state machine
    stats.py           rolling stats, drop tracker, rate estimator
    io_serial.py       SerialSource and FileSource
    exporters.py       CsvExporter and RawBinaryExporter
    plotting.py        matplotlib live plotter (lazy imported)
    analysis.py        FFT, rolling stats helpers, histogram
    cli.py             argparse entry point
  tests/
    conftest.py
    test_crc16.py
    test_protocol.py
    test_framer.py
    test_stats.py
    test_analysis.py
    test_cli_smoke.py
```

For protocol details see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md).

## Credits

The original concept for Tiny Chaos came from **The Don**.
