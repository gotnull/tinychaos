# Entropy Capture Pipeline

Authoritative reference for the tinychaos zener-noise capture pipeline. This document defines the on-the-wire protocol, the host reader design, the validation workflow, and the hardware safety envelope.

If you are implementing or debugging any part of the capture chain, this is the document of record. Older docs (`docs/analysis-app.md`, `docs/firmware-notes.md`) are superseded by this one and now redirect here.

---

## 1. Project purpose

Capture wideband noise from a reverse-biased zener diode operating in avalanche breakdown, digitise it with an STM32 ADC, and stream samples to a macOS host for live diagnostics and offline analysis. The goal is a measurable, instrumented entropy source where every stage is observable: the analogue front-end, the ADC, the transport, and the host-side decoding.

The pipeline replaces an earlier ad-hoc workflow (ASCII UART dumps with manual hex editing) with a framed binary protocol that has CRC, sequence numbers, microsecond timestamps, and resync. The host can detect drops, count bad CRCs, recover from corruption, and estimate sample rate from two independent clocks. None of that was possible with the previous approach.

The host tooling is Python. There is no GUI in v1 beyond a small live matplotlib panel. The previous C# analysis-app plan is dropped.

## 2. Hardware assumptions

- STM32 NUCLEO-H753ZI development board, or a pin- and peripheral-compatible STM32H7 board. Other STM32 families are likely to work with port-level changes to ADC and DMA setup.
- USB cable from the board to a macOS host. The board's on-board ST-LINK exposes a USB CDC virtual COM port, which is the primary transport.
- Analogue front-end on a breadboard, populated per [docs/hardware-design.md](hardware-design.md): zener + bias + AC coupling + two-stage amplifier + ADC protection.
- Two analogue channels are sampled: the amplified zener signal on ADC1 INP15 (PA3, the A0 pin) and a fixed mid-rail reference divider on ADC1 INP10 (PC0, the A1 pin). They are paced by a TIM3 trigger and DMA'd as channel 0 / channel 1. See [firmware/nucleo-h753zi/Core/Src/entropy_app.c](../firmware/nucleo-h753zi/Core/Src/entropy_app.c).
- Bias supply for the zener is 12 V to 18 V, derived from either two 9 V batteries in series or a DC-DC boost module from the +5 V breadboard rail. See [docs/filtering-and-power.md](filtering-and-power.md).

## 3. Zener avalanche-noise source

A reverse-biased zener diode breaks down at its rated voltage Vz. The mechanism depends on Vz:

- Vz below about 5.5 V: Zener (tunneling) breakdown. Quiet.
- Vz above about 5.5 V: avalanche breakdown. The avalanche multiplication factor varies cycle-to-cycle, producing wideband, near-white noise riding on the DC breakdown voltage.

The 1N4735 (6.2 V) is the primary entropy device. The 1N4732 (4.7 V), 1N4733 (5.1 V), and 1N4734 (5.6 V) are stocked for transition characterisation. Typical AC noise at the noise tap with 50 to 200 uA bias current is 100 uV to 2 mV RMS, broadband from a few Hz to well above 100 kHz. Total amplifier gain in the recommended chain is approximately 1000, which lands the signal in the lower volts range, suitable for a 3.3 V single-supply ADC.

For circuit details, bias-resistor sizing, and component-to-BOM cross-reference, see [docs/hardware-design.md](hardware-design.md).

## 4. ADC safety notes

The STM32H7 ADC inputs share the analogue rail with the rest of the chip. Three rules apply at the pin:

- Never exceed the absolute-maximum input range. For STM32H7 the limit is `Vdda + 0.3 V`, which is 3.6 V at the standard `Vdda = 3.3 V`. The negative absolute-max is `-0.3 V`.
- Always go through a protection network. Series resistance limits fault current, Schottky clamps bound the voltage, and an anti-alias cap limits bandwidth. See [docs/adc-protection.md](adc-protection.md).
- Use a single analogue ground reference. Multiple paths to GND create loops that pick up mains hum at the worst possible point in the signal chain.

The amplifier output can saturate to either supply rail under transient or DC offset. Do not rely on the amplifier alone to keep the ADC pin in range. The clamp network is independent of the amplifier.

## 5. Warning: STM32 ADC input must never exceed 3.3 V

This warning is repeated here because it is the single most important constraint in the project. Connecting an unprotected zener-circuit node directly to an STM32 ADC pin will damage the pin.

The required network at the ADC pin is:

```
amplifier output ----[R_series 1k]----+----[ADC pin PA0 or PA3]
                                      |
                                      +--[BAT46]--> +3.3 V (cathode to rail)
                                      |
                                      +--[BAT46]--> GND   (anode to GND)
                                      |
                                      +---[C_aa 100 nF]--- GND
```

The Schottky forward voltage of about 0.3 V clamps the input at `3.3 + 0.3 = 3.6 V` upper and `0 - 0.3 = -0.3 V` lower, both within absolute-max. R_series of 1 kohm limits any clamp current to safe values. Layout and rationale are in [docs/adc-protection.md](adc-protection.md).

## 6. Recommended analogue signal chain

The path from zener to ADC pin:

```
+12 V to +18 V clean bias supply (battery preferred)
        |
        v
bias trimpot (100 kohm multiturn cermet, fine-tunes reverse current 50 to 200 uA)
        |
        v
1N4735 zener (reverse biased into avalanche)
        |
        v   noise tap, DC ~6.2 V with mV of AC noise on top
        |
1 uF MKT film AC coupling cap
        |
        v
stage 1 amplifier: NE5534P single low-noise op-amp, gain ~100, mid-rail biased
        |
        v
stage 2 amplifier: LM833N dual low-noise op-amp, gain ~10, with lowpass at ~48 kHz
        |
        v   ~1 V RMS centred on mid-rail (~1.65 V)
        |
ADC protection: 1 kohm series, two BAT46 Schottky clamps, 100 nF anti-alias to GND
        |
        v
STM32 NUCLEO-H753ZI, ADC1 INP15 (PA3) for zener, ADC1 INP10 (PC0) for baseline divider
```

The baseline divider is two 100 kohm resistors from +3.3 V to GND meeting at a tap that goes through its own 1 kohm + 100 nF + Schottky clamp network into ADC1 INP10 (PC0). It provides a clean reference channel for every capture run, so the host can subtract baseline drift from the zener channel.

Component values, bias calculations, and feedback network sizing are in [docs/hardware-design.md](hardware-design.md).

## 7. USB CDC transport design

The STM32 NUCLEO-H753ZI exposes a USB CDC virtual COM port through its on-board ST-LINK. This is the primary transport. The host sees it under different names depending on the OS:

- macOS: `/dev/tty.usbmodemXXXXX`
- Linux: `/dev/ttyACM0` (or `/dev/ttyUSB0` with some drivers)
- Windows: `COMx`, where `x` is whatever Windows assigned (visible in Device Manager under Ports)

Key properties of the USB CDC link in this project:

- The host-side `baud` parameter is ignored over USB CDC. The link runs at USB FS speed (12 Mbps theoretical, around 1 MB/s practical). The CLI accepts `--baud` for compatibility with the UART fallback.
- Effective per-packet latency is dominated by the host's USB scheduler, typically 1 to 8 ms. The transport adds bounded jitter that the host can measure by comparing STM32-supplied timestamps to host-arrival timestamps.
- The firmware-side transmit path uses `CDC_Transmit_FS` and must handle the `BUSY` state cleanly. The strategy is a small bounded ring buffer in `firmware/Core/Src/usb_stream.c` between the ADC callback (producer) and the USB endpoint completion (consumer). On full ring, the producer drops the packet and increments a counter. Lost whole packets are detectable by the host through SEQ gaps; sample loss inside a packet is not, so drop-and-count is preferred over back-pressure.
- A UART fallback exists at USART3 at 921 600 baud, DMA TX, selected at compile time by a `#define`. UART is for bring-up before USB CDC is wired in. UART throughput is around 115 kB/s, sufficient for 10 kHz two-channel capture but not for higher rates.

## 8. Binary packet format

This section is the canonical wire-format reference. The firmware encoder and the Python decoder both target this layout exactly.

```
Offset  Field      Size       Notes
------  ---------  ---------  ---------------------------------------------------
0       MAGIC      2 bytes    Literal 0xDA 0x7A, not endian-swapped
2       VERSION    1 byte     Protocol version, currently 1
3       FLAGS      1 byte     Reserved, currently 0
4       SEQ        4 bytes    uint32 little-endian, monotonically increasing
8       TIME_US    4 bytes    uint32 little-endian, STM32 microsecond timestamp
12      COUNT      2 bytes    uint16 little-endian, number of uint16 samples
14      SAMPLES    2*COUNT    uint16 little-endian ADC samples
14+2N   CRC16      2 bytes    uint16 little-endian, CRC-16/CCITT-FALSE
```

All multi-byte integer fields are little-endian. The CRC covers VERSION through the last sample inclusive, that is, 12 + 2*COUNT bytes. The CRC does not cover MAGIC or itself.

Encoded packet size: `16 + 2 * PACKET_SAMPLE_COUNT` bytes. At `PACKET_SAMPLE_COUNT = 256` (the default), each packet is 528 bytes.

Default rate budget:

- ADC sample rate: 10 000 samples per channel per second.
- Channels: 2 (zener + baseline divider).
- Total raw rate: 20 000 samples per second, 40 000 bytes per second.
- Packets per second: 20 000 / 256 = 78.125, rounded to 78.
- Wire rate: 78 * 528 = 41 184 bytes per second, comfortably below both USB CDC and UART ceilings.

If the user later raises the ADC rate, `PACKET_SAMPLE_COUNT` should be increased in proportion so the per-packet rate stays around 50 to 200 per second. That keeps SEQ-gap detection latency under 20 ms while not flooding the CDC scheduler with tiny packets.

## 9. Host Python reader design

The host implementation lives under `tools/` as a Python package `tinychaos`. Module layout:

| Module              | Responsibility                                                  |
|---------------------|------------------------------------------------------------------|
| `protocol.py`       | CRC, encode, decode, exception hierarchy. Canonical packet API. |
| `framer.py`         | Streaming resync state machine. Tolerates corruption.           |
| `stats.py`          | Rolling stats, drop tracker, dual-clock rate estimator.         |
| `io_serial.py`      | pyserial-backed `SerialSource` and offline `FileSource`.        |
| `exporters.py`      | CSV and raw-binary exporters as context managers.               |
| `plotting.py`       | Lazy-imported matplotlib. CLI gracefully degrades if absent.    |
| `analysis.py`       | Rolling mean/std, Z-score, histogram, FFT PSD.                  |
| `cli.py`            | argparse entry point, runs the live capture loop.               |

The CLI is the user-facing surface:

```
python -m tinychaos.cli --port <PORT> --csv out.csv
python -m tinychaos.cli --port <PORT> --plot
python -m tinychaos.cli --port <PORT> --duration 60 --csv baseline.csv --validation-label baseline
python -m tinychaos.cli --replay capture.bin --csv replay.csv
```

The CLI orchestrates: open source, feed bytes into framer, route emitted packets through stats and exporters, on exit print a summary (packets, bad CRCs, drops, STM32 rate, host rate, per-channel min/max/mean/std).

CSV column schema is canonical and enforced by `exporters.CsvExporter`:

```
host_time, packet_seq, stm32_time_us, sample_index, channel_index, adc_value, validation_label
```

`channel_index` is 0 for the zener channel (IN0) and 1 for the baseline divider (IN3). `validation_label` is taken from the `--validation-label` flag and is empty if not supplied.

## 10. Dropped packet detection

Every packet carries a 4-byte SEQ field that increments by 1 per packet on the device side. The host maintains a `DropTracker` in `tinychaos.stats`. On each received packet:

```
expected = last_seq + 1
if seq > expected:
    drops += (seq - expected)
last_seq = seq
```

Wraparound at `seq == 2^32` is handled by treating arithmetic as modulo 2^32. Sustained drop rate is reported in the CLI summary. Drops do not poison the rest of the capture: the framer just continues delivering whatever packets do arrive.

Implications:

- If the firmware drops because the USB ring buffer is full, the host sees a SEQ gap and counts it.
- If a packet is corrupted on the wire and fails CRC, the host treats it as a bad CRC, not a drop, and does not advance `last_seq` from that packet.
- If the framer skips bytes during resync, it does not increment the drop counter; that is reported separately via `ResyncDropped(n_bytes)`.

The three counters (drops, bad CRC, resync-skipped bytes) are kept separate so the user can diagnose root cause: drops mean firmware back-pressure or USB scheduling, bad CRC means a bit flip on the wire, resync bytes mean stream desync.

## 11. CRC validation

The protocol uses CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF, no input reflection, no output reflection, no final XOR. Known-answer on `b"123456789"` is `0x29B1`.

This variant is preferred over init-zero because it detects all-zero packets that init-zero would silently accept as valid. A stuck-low UART line is a real failure mode, and init-0xFFFF makes it visible.

Both the host (`tinychaos.protocol.crc16_ccitt_false`) and the firmware (`firmware/Core/Src/entropy_protocol.c::crc16_ccitt_false`) implement the same algorithm. Each side has a known-answer test against `b"123456789"` -> `0x29B1`. If either implementation drifts, both KAT tests fail and the divergence is caught immediately.

CRC failures on the host are not retried (UART/CDC has no retransmission). They are counted and surfaced in the CLI summary.

## 12. Resynchronisation strategy

The host framer is a streaming state machine that tolerates arbitrary corruption: leading garbage, mid-packet bit flips, magic bytes flipped, packet truncation, double magics, anything. States:

```
SEARCH_MAGIC0  -- looking for 0xDA byte
SEARCH_MAGIC1  -- saw 0xDA, now looking for 0x7A
READ_HEADER    -- have MAGIC, accumulating 12 header bytes
READ_BODY      -- header parsed, accumulating 2*COUNT sample bytes
READ_CRC       -- accumulating 2 CRC bytes
VALIDATE       -- compute CRC, emit PacketReceived or BadCrc
```

On any mismatch the framer rewinds by one byte from the last false start and resumes searching for MAGIC. The framer maintains a counter of bytes skipped during resync and emits `ResyncDropped(n_bytes)` events so the host CLI can report on stream health.

Critical property tested in `tools/tests/test_framer.py`: feeding the same byte stream in one-byte chunks, fixed chunks, and random chunks yields the same event sequence. Hypothesis-driven chunk-size invariance gives confidence the framer is correctly stateful.

A second tested property: after any single bit-flip in any byte of any packet, the framer either emits BadCrc on that packet (most cases) or resyncs and emits ResyncDropped (when the bit flip lands in the MAGIC). It never silently emits a corrupted PacketReceived.

## 13. Timing and sample-rate measurement

The host measures sample rate two independent ways and reports both:

- STM32-derived rate: `stm32_rate_hz = sum(count over window) / ((time_us_last - time_us_first) / 1e6)`. This is what the STM32 thinks it is sampling at. It is internally consistent and jitter-free.
- Host-derived rate: `host_rate_hz = sum(count over window) / (host_wall_last - host_wall_first)` using `time.monotonic()` at packet arrival on the host.

`time_us` is a uint32 of microseconds, so it wraps every 2^32 / 1e6 = 4 295 seconds = about 71.6 minutes. `RateEstimator` handles wraparound by treating the difference as modulo 2^32 and rejecting deltas that imply a negative or implausibly large interval.

Sources of divergence between the two rates:

| Divergence                            | Likely cause                                       |
|---------------------------------------|----------------------------------------------------|
| Both rates equal but wrong            | Wrong `ADC_SAMPLE_RATE_HZ` config                  |
| stm32 stable, host fluctuates         | USB scheduling jitter, fine to ignore short term   |
| stm32 < host by sustained amount      | Sustained USB back-pressure, the ring buffer is full and the host is replaying older packets... actually impossible since drops do not retransmit, see next row |
| stm32 < host                          | Implausible. Investigate.                          |
| stm32 > host                          | STM32 clock fast relative to host, check HSE crystal vs HSI |
| Large skew (>1000 ppm)                | Clock source misconfigured on either end           |

The CLI prints both rates on exit. A persistent skew greater than a few hundred ppm is flagged.

## 14. Baseline comparison strategy

The most useful diagnostic is comparing captures from different physical input configurations. Each configuration isolates a particular noise source.

Five comparison sources, each labelled in the CSV via `--validation-label`:

| Label      | Wiring                                          | What it isolates                                | Expected signature                                              |
|------------|-------------------------------------------------|--------------------------------------------------|-----------------------------------------------------------------|
| `shorted`  | ADC input tied directly to mid-rail divider tap | ADC quantisation only                            | Tight distribution at mid-rail, std on order of 1 LSB           |
| `divider`  | Same as the permanent baseline channel          | ADC + divider thermal noise                      | Slightly broader than `shorted`, no spectral peaks              |
| `floating` | ADC input open-circuit                          | Mains pickup, antenna effects                    | Large 50 Hz peak in FFT, plus harmonics at 100 and 150 Hz       |
| `zener`    | Full zener chain in normal operation            | Avalanche noise plus everything below it         | Broadband Gaussian, std at least 10 to 100x the `shorted` std   |
| `battery`  | Same as `zener` but bias supply is 2x 9 V batt  | Removes any PSU ripple from `zener`              | Cleaner FFT than `zener` on a wall PSU; difference is PSU noise |

Recommended duration: 30 to 60 seconds per source for stable statistics.

Workflow:

1. Wire the configuration.
2. Run `python -m tinychaos.cli --port ... --duration 60 --csv labels/shorted.csv --validation-label shorted`.
3. Repeat for each of the five labels.
4. Run an offline comparison through `tinychaos.analysis` (loaded by a small script in `tools/scripts/compare.py`, to be added in step 9 of the implementation plan).

The five captures together let the user separate avalanche noise from PSU ripple, mains hum, ADC artefacts, USB jitter, and serial transport corruption. Each diagnostic is grounded in physical wiring, not in code-side flags.

## 15. Future GUI ideas (out of scope for v1)

These are recorded for context only. None of them ship in v1.

- A dedicated Qt or Avalonia GUI with a scope-like waveform view, a persistent histogram with hue-encoded recency, a spectrogram, and live overlay of statistics.
- Side-by-side capture comparison: load two `validation_label`-tagged CSVs and show difference histograms and difference spectra.
- Live randomness-quality indicators: NIST SP 800-90B health tests, ent-style entropy bits per sample, a single-number summary.
- An EGD (Entropy Gathering Daemon) protocol server so the captured stream can be plumbed into `/dev/urandom` or similar pools on the host.
- Cryptographic whitening output: von Neumann bit debias, or SHA-256 squeeze, producing a uniform random byte stream on a named pipe.

These would all sit downstream of the v1 pipeline. None require changes to the wire protocol.
