# tinychaos tools

Host-side Python tooling for the tinychaos zener-noise entropy capture pipeline. Reads framed binary packets from the STM32 firmware over USB CDC (or UART), validates CRCs, recovers from corruption, exports CSV and raw binary, and optionally plots live.

Target platform: macOS. Should also work on Linux. Windows untested.

## Setup

```
cd tools
python3 -m venv .venv
source .venv/bin/activate
pip install -e .[dev]
```

Add the `plot` extra if you want live plotting:

```
pip install -e .[dev,plot]
```

## Run

Live capture from the board over USB CDC:

```
python -m tinychaos.cli --port /dev/tty.usbmodem<TAB> --csv out.csv
```

Capture with a validation label and a duration cap:

```
python -m tinychaos.cli --port /dev/tty.usbmodem<TAB> --duration 60 --csv labels/zener.csv --validation-label zener
```

Replay a previously captured raw binary file (no hardware needed):

```
python -m tinychaos.cli --replay capture.bin --csv replay.csv
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
