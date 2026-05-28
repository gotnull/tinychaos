# Host analysis applications

The project has two host implementations that speak the same wire protocol byte-for-byte:

- **Python** under [`../tools/`](../tools/). Preferred on macOS and Linux. Has matplotlib live plotting, FFT analysis, rolling Z-score, and a full pytest suite. See [`../tools/README.md`](../tools/README.md).
- **C# / .NET 8** under [`../analysis/`](../analysis/). Preferred on Windows; runs cross-platform. CLI-only for v1, with a parallel xUnit suite. See [`../analysis/README.md`](../analysis/README.md).

Pick whichever host matches your operating system and preferred toolchain. The protocol layer is identical on both sides; any captures produced by one are readable by the other through the `--replay` mode of either CLI, and the CSV column schema is shared.

## Choosing between them

| Concern                              | Python                                     | C# CLI                                   | C# GUI (Avalonia)                            |
|--------------------------------------|--------------------------------------------|------------------------------------------|----------------------------------------------|
| Setup overhead                       | `python -m venv` + `pip install`           | install .NET 8 SDK, then `dotnet build`  | install .NET 8 SDK, then `dotnet run --project src/TinyChaos.Gui` |
| Live plotting                        | Yes (matplotlib, optional `plot` extra)    | n/a (no GUI)                             | Yes (custom Avalonia DrawingContext canvases, 30 Hz waveform, 10 Hz histogram) |
| FFT and offline analysis             | Yes (`tinychaos.analysis`)                 | Not in v1                                | Not in v1                                    |
| CSV export                           | Yes                                        | Yes                                      | Not in v1 (CLI is the canonical CSV producer)|
| Raw binary export and replay         | Yes                                        | Yes                                      | Not in v1 (use CLI for replay)               |
| Sequence-gap and CRC error counting  | Yes                                        | Yes                                      | Yes                                          |
| Sample-rate dual estimation          | Yes                                        | Yes                                      | Yes                                          |
| Test framework                       | pytest, Hypothesis                         | xUnit                                    | (shares xUnit via shared `TinyChaos.Protocol` and `TinyChaos.Host.Stats`) |
| Cross-platform                       | macOS, Linux primary                       | Windows, macOS, Linux                    | Windows, macOS, Linux from a single codebase |

## Wire-format authority

Three reference implementations of the protocol exist:

- Python: `tools/src/tinychaos/protocol.py` (`crc16_ccitt_false`, `encode_packet`, `decode_packet`).
- C: `firmware/Core/Src/entropy_protocol.c` (`crc16_ccitt_false`, `entropy_packet_encode`, `entropy_packet_decode_header`).
- C#: `analysis/src/TinyChaos.Protocol/PacketCodec.cs` (static `Encode`, `Decode`).

All three are pinned to the same cross-implementation parity vector. For the input
`seq=0x11223344, time_us=0x55667788, samples=[0x0102, 0x0304, 0x0506, 0x0708]`, every
implementation produces exactly:

```
DA 7A 01 00 44 33 22 11 88 77 66 55 04 00 02 01 04 03 06 05 08 07 92 4D
```

This is verified by:

- Python: `tools/tests/test_protocol.py::test_explicit_byte_layout`
- C: `firmware/test/test_protocol_host.c` (the on-host firmware self-test)
- C#: `analysis/tests/TinyChaos.Tests/PacketTests.cs::ParityWithPythonAndFirmware_FixedInput`

If you change the wire format, change all three implementations together and verify the parity tests still pass.

For the full protocol spec see [ENTROPY_CAPTURE_PIPELINE.md](ENTROPY_CAPTURE_PIPELINE.md) section 8.
