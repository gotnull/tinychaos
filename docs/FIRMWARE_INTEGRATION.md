# Firmware integration guide

This document is for anyone bringing an existing STM32 firmware onto the tinychaos host pipeline. It captures what was discussed when comparing the two sides of the project: the existing STM32 program that streams raw ADC samples, and the host stack that expects framed binary packets.

If you have not written any firmware yet and want to start from scratch, the right starting point is [firmware/README.md](../firmware/README.md). This doc assumes you already have a working firmware that drives ADC + DMA on an STM32 and you want to feed its output into the GUI / CLI / Python tooling.

## The two sides at a glance

| Side | What you have | What you do not have |
|---|---|---|
| **Existing firmware** (typical setup) | Working CubeMX project, generated `main.c` and HAL init, USB CDC middleware that actually transmits bytes, ADC + DMA configured in circular mode with half-complete and full-complete callbacks already firing, a `.bin` you can flash today. | A protocol that lets the host recover from dropped bytes or detect corruption. |
| **This repo's `firmware/` directory** | Portable C protocol module (CRC, packet encode, decode), USB CDC and UART ring-buffer transmitters with drop-and-count back-pressure handling, integration documentation, a host self-test that verifies byte-for-byte parity with the Python and C# reference implementations. | A CubeMX project, generated `main.c`, USB middleware, linker script, ADC capture implementation, a flashable binary. |

The protocol library is the bit worth sharing. The rest of `firmware/` is scaffolding for the case where someone starts a new CubeMX project from scratch.

## So integration goes the other direction

You **keep your existing firmware project**. You drop three of our files into it. You change one function call inside each of your DMA callbacks. That's the entire integration.

### Files to copy from our tree into yours

| Our file | Where to put it in your tree | What it does |
|---|---|---|
| [firmware/Core/Inc/entropy_config.h](../firmware/Core/Inc/entropy_config.h) | next to your other headers | compile-time constants (MAGIC bytes, protocol version, packet size limits) |
| [firmware/Core/Inc/entropy_protocol.h](../firmware/Core/Inc/entropy_protocol.h) | next to your other headers | the public API of the protocol module |
| [firmware/Core/Src/entropy_protocol.c](../firmware/Core/Src/entropy_protocol.c) | next to your other sources, add it to your Makefile / CubeIDE build list | implementation: CRC-16/CCITT-FALSE, `entropy_packet_encode()`, `entropy_packet_decode_header()`, self-test |

All three are portable C99. No HAL dependencies. They compile equally well with `arm-none-eabi-gcc` (your target) and with host `gcc` / `clang` (host self-test).

Optionally, also copy:

- [firmware/Core/Inc/usb_stream.h](../firmware/Core/Inc/usb_stream.h) + [firmware/Core/Src/usb_stream.c](../firmware/Core/Src/usb_stream.c). This is a small ring buffer between your producer and `CDC_Transmit_FS`. Without it, if the host USB stack is momentarily busy and your callback calls `CDC_Transmit_FS` again, your bytes are silently dropped on the floor. With it, the dropped packets get counted and the host sees a SEQ gap (which is the whole point of having SEQ numbers). If you already have your own back-pressure handling, you can skip this.

### The change to your DMA callbacks

Today your half-complete and full-complete callbacks probably look like this:

```c
void HAL_ADC_ConvHalfCpltCallback(ADC_HandleTypeDef *hadc)
{
    CDC_Transmit_FS((uint8_t*)adc_buffer, 1024);  // first half, 512 samples * 2 bytes
}

void HAL_ADC_ConvCpltCallback(ADC_HandleTypeDef *hadc)
{
    CDC_Transmit_FS((uint8_t*)(adc_buffer + 512), 1024); // second half
}
```

You wrap each send in one call to `entropy_packet_encode()`:

```c
#include "entropy_config.h"
#include "entropy_protocol.h"
// (and "usb_stream.h" if you copied that file in too)

static uint8_t  pkt_out[ENTROPY_PACKET_MAX_BYTES];
static uint32_t seq;

static inline uint32_t now_us(void)
{
    // Use whatever 1 MHz free-running counter you have. Common choices:
    //   - DWT->CYCCNT divided by core MHz
    //   - a TIM5 / TIM2 / TIM7 instance in basic free-running mode at 1 MHz
    // The host can tolerate uint32 wraparound (every ~71 minutes); it just
    // needs to be monotonic between consecutive packets.
    return __HAL_TIM_GET_COUNTER(&htim5);
}

void HAL_ADC_ConvHalfCpltCallback(ADC_HandleTypeDef *hadc)
{
    size_t n = entropy_packet_encode(
        pkt_out, sizeof pkt_out,
        seq++, now_us(),
        &adc_buffer[0], 512);          // first 512 samples
    if (n > 0) {
        CDC_Transmit_FS(pkt_out, n);   // or usb_stream_send(pkt_out, n) if you copied usb_stream.c
    }
}

void HAL_ADC_ConvCpltCallback(ADC_HandleTypeDef *hadc)
{
    size_t n = entropy_packet_encode(
        pkt_out, sizeof pkt_out,
        seq++, now_us(),
        &adc_buffer[512], 512);        // second 512 samples
    if (n > 0) {
        CDC_Transmit_FS(pkt_out, n);
    }
}
```

That is the entire firmware change.

If you copied `usb_stream.c` in, also add one line inside `CDC_TransmitCplt_FS` in your `usbd_cdc_if.c` so the ring buffer knows the previous transfer finished and the next packet can go:

```c
// Inside CDC_TransmitCplt_FS, after the user-code begin marker:
extern void usb_stream_on_tx_complete(void);
usb_stream_on_tx_complete();
```

## What you get on the host side, for free

Once your firmware sends framed packets, every host-side feature works automatically. None of it requires further firmware changes.

- **GUI live view** ([analysis/src/TinyChaos.Gui](../analysis/src/TinyChaos.Gui)): live waveform per channel at 60 fps, cumulative histogram, per-channel statistics, status pills showing packet count, CRC errors, dropped packets, resync bytes, dual-clock sample-rate estimate.
- **Recording**: the GUI's Record button writes the exact byte stream to a `.bin` file in [samples/](../samples/). Stop recording (or disconnect) flushes the file. The file appears in the Samples tab immediately.
- **Replay**: any `.bin` file (whether seeded in the repo or recorded yourself) can be replayed through the same framer that the live capture uses, so you can inspect captures later, share them, or run them through the Python analysis pipeline.
- **CLI replay and CSV export**: the Python CLI ([tools/](../tools/)) and the C# CLI ([analysis/src/TinyChaos.Host](../analysis/src/TinyChaos.Host)) both accept `--replay path/to/capture.bin` and export per-sample CSV with packet sequence, microsecond timestamp, channel index, ADC value, and a validation label column.
- **Corruption recovery**: if a byte goes missing on the USB cable or there is an ESD glitch, the framer scans for the next MAGIC and continues. The host's CRC counter goes up; it does not crash or lose alignment forever.

## Why the framing matters

Without framing, dropping a single byte on the wire misaligns every subsequent int16 sample for the rest of the session. The PC starts reading the high byte of sample N as the low byte of sample N+1 and there is no way to detect or recover. You have to restart both ends.

With framing (16 bytes of overhead per packet: 2 magic + 1 version + 1 flags + 4 SEQ + 4 microsecond timestamp + 2 sample count + 2 CRC), the host:

- Detects every corrupted packet via CRC and counts them
- Detects every dropped packet via the SEQ field and counts them
- Detects every desync via the MAGIC scanner and counts the skipped bytes
- Estimates sample rate two independent ways (STM32 clock via `time_us`, host wall clock) and warns if they diverge

The 16 bytes per packet is roughly 3% overhead at 256 samples per packet (528 bytes total). That is the price of being robust against everything USB CDC and the host scheduler can do to your bytes.

## Your spike-encoding plan: same protocol carries it

When you later get to converting samples into 0/1 spike bits and packing them into 8-byte blocks, you do not need a different transport. You set bit 0 of the `FLAGS` byte to `1` to mean "this packet's payload is packed spike bits, not 16-bit ADC samples". Set `COUNT` to the number of bytes or bits in the payload (you decide, just document it). The host parser already exposes `Flags` and we never reserved that bit for anything else.

The framing, sequencing, timestamping, CRC, drop detection, replay, recording, and GUI panels all keep working unchanged. Add a small branch on the host that interprets payload as bits when the flag is set, and your spike pipeline plugs into the same infrastructure.

## The wire format, in one block

For reference. Every field is little-endian. This is the canonical version of the spec; both the Python implementation (`tinychaos.protocol`) and the C# implementation (`TinyChaos.Protocol.PacketCodec`) and the C implementation here (`entropy_protocol.c`) match it byte for byte.

```
offset  field      size      value / source
------  ---------  --------  ------------------------------------------------
0       MAGIC      2 bytes   literal 0xDA 0x7A
2       VERSION    1 byte    0x01
3       FLAGS      1 byte    0x00 (reserved; spike-mode candidate: bit 0)
4       SEQ        4 bytes   uint32 LE, +1 each packet
8       TIME_US    4 bytes   uint32 LE, STM32 microsecond timestamp
12      COUNT      2 bytes   uint16 LE, number of uint16 samples in this packet
14      SAMPLES    2*COUNT   little-endian uint16 ADC samples
14+2N   CRC16      2 bytes   uint16 LE, CRC-16/CCITT-FALSE
```

CRC-16/CCITT-FALSE parameters: polynomial 0x1021, initial value 0xFFFF, no reflection, no final XOR. Known-answer: `crc16("123456789") == 0x29B1`.

## Verifying your integration without leaving the desk

After you have made the firmware change, before you even flash:

1. Build your firmware. If `entropy_protocol.c` compiles for your target, the protocol module is happy.
2. From this repo's root, run `make -C firmware test`. That builds and runs the same module against `gcc`/`clang` on the host. If it prints "all checks passed" you know your binary is producing byte-identical output for the same inputs.
3. After flashing, plug in the NUCLEO and run the GUI: `dotnet run --project analysis/src/TinyChaos.Gui -c Release` (Windows: `dotnet run --project analysis\src\TinyChaos.Gui -c Release`). Pick the COM port (Windows) or `/dev/tty.usbmodem*` (macOS) or `/dev/ttyACM0` (Linux) in the Port dropdown, hit Connect. You should see live samples in the WAVEFORM panel and the `pkts` pill in the status footer climbing, with `bad_crc` and `drops` staying at zero.

If `bad_crc` is going up: check your `time_us` is monotonic and your `seq` is incrementing once per packet. If `drops` is going up but `bad_crc` stays at zero: your USB queue is overflowing, copy in [usb_stream.c](../firmware/Core/Src/usb_stream.c) and route through `usb_stream_send` instead of `CDC_Transmit_FS` directly.

## Summary

- Your firmware stays. Our `firmware/` is mostly empty scaffolding plus one real piece: the protocol module.
- Copy three files (`entropy_config.h`, `entropy_protocol.h`, `entropy_protocol.c`) into your project.
- Wrap each DMA-callback `CDC_Transmit_FS` call with one `entropy_packet_encode` call.
- That is the entire integration. Spike encoding plan continues to work on top of the same wire format.
