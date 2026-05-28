# STM32 Firmware Notes

Target board: NUCLEO-H753ZI (STM32H753ZIT6, Cortex-M7 at 480 MHz, 16-bit ADC, plenty of SRAM and DMA channels).

This document is the firmware plan, not the firmware itself. The actual code will live in `firmware/` once written.

## Goals

1. Sample the analogue noise input at a known, jitter-free rate.
2. Stream samples to the host PC over USB CDC virtual COM (via the on-board ST-LINK).
3. Sample the baseline channel concurrently for differential characterisation.
4. Be measurable: provide a debug pin that pulses every N samples so a scope can confirm timing.

## ADC configuration

```
peripheral:   ADC1
channels:     IN0 on PA0  (zener signal path, post-amplifier, post-protection)
              IN3 on PA3  (mid-rail baseline divider, same protection network)
resolution:   12-bit (16-bit is available; 12-bit is plenty given the SNR
              of the analogue chain, and gives faster sample times)
sample time:  long enough for 1 kohm source impedance to settle the SAR.
              Per STM32H7 datasheet, 8.5 ADC clock cycles is conservative.
              Use SMP = 16.5 cycles for safety margin.
ADC clock:    derived from PLL2 or HSI to give exactly the desired sample rate
trigger:      TIM2 (or similar) overflow at the chosen sample rate, hardware-triggered
              to eliminate software jitter.
DMA:          ADC1 to a circular buffer in DTCM RAM, double-buffered.
              On half-complete, transmit first half over UART; on complete,
              transmit second half; ping-pong.
```

## Sample-rate options

| Rate         | Bandwidth captured (Nyquist) | UART baud needed (12-bit samples, 2 channels) | Comments                              |
|--------------|------------------------------|------------------------------------------------|---------------------------------------|
|     1 kHz    |   500 Hz                     |  ~40 kbaud raw                                 | Low rate, useful for first verification |
|    10 kHz    |     5 kHz                    | ~400 kbaud raw                                 | Default for noise characterisation    |
|   100 kHz    |    50 kHz                    | ~4 Mbaud raw                                   | Wide noise BW; needs USB CDC at higher rates or binary framing |
|     1 MHz    |   500 kHz                    | requires USB FS bulk (~12 Mbps) or HS         | Maximum useful; only via USB bulk, not CDC at 921600 |

Start at 10 kHz. Move up once the C# host can keep up.

## Streaming protocol

Frame format on the wire (binary, little-endian, no escapes):

```
[ 0xA5 0x5A ][ seq_uint16 ][ count_uint16 ][ samples...uint16... ][ crc16 ]

  0xA5 0x5A     magic header, 2 bytes
  seq           rolling 16-bit frame counter, host detects drops
  count         number of uint16 samples in this frame
  samples       count * 2 bytes, alternating zener / baseline channel
  crc16         CRC-16/CCITT over header through samples
```

The host C# app frames on the magic, verifies CRC, and reorders by seq.

If a frame is dropped (CRC fail or seq gap), the host marks the gap and continues. The noise analysis is robust to small contiguous drops as long as they are detected.

## Memory layout

```
ADC1 DMA buffer:    8 kB in DTCM (DMA1 to DTCM is supported on H7)
                    arranged as two 4 kB halves for ping-pong
                    each 4 kB = 2048 uint16 samples = 1024 samples per channel
UART TX buffer:     16 kB in regular SRAM
                    DMA2 stream from this buffer to USART3 TX (LPUART1 also OK)
```

## Debug pins

- PB0: toggle on every ADC half-complete callback. Scope shows constant 5 ms period at 10 kHz sampling and 1024-sample halves. Any jitter here means hardware-trigger setup is wrong.
- PB14 (on-board LED1, blue): blink at 1 Hz to confirm firmware is running.

## Build chain

```
toolchain:     arm-none-eabi-gcc, latest stable
build system:  CMake with stm32-cmake or PlatformIO; either is fine
HAL:           STM32 HAL or LL. LL is recommended for the ADC/DMA/timer
               path because the HAL adds latency in the IRQ handlers and
               we care about deterministic timing on the ADC trigger.
debugger:      ST-LINK on the NUCLEO board, OpenOCD or pyOCD
```

## What to verify first

1. Constant sample rate. Scope on PB0 should show a clean square wave at the expected rate.
2. Both channels reading the right voltage. Read the baseline channel and confirm it sits at 1.65 V (mid-rail). Read the zener channel at DC and confirm the AC noise rides on the same DC bias.
3. UART throughput. Stream a known counter pattern instead of ADC samples for a minute; host counts frames received vs expected, must match.
4. Real noise. Once 1 to 3 are clean, switch to ADC samples and verify the host sees the expected RMS noise voltage on the zener channel above the baseline.

## Open questions for the firmware iteration

- USB CDC vs raw USART through the ST-LINK virtual COM. ST-LINK is essentially a USART-to-USB bridge, so it should not matter, but USB CDC native on the H7 (USB OTG FS, ID pin on PA10/PA11) bypasses the ST-LINK altogether and can go to USB HS via the on-board PHY if needed. Decide once the 921600 baud CDC saturates.
- Sample word size. 12-bit data left-aligned in 16-bit transfer is the convention. Right-aligning saves 4 bits per sample but complicates host parsing. Default: left-aligned.
- Calibration. STM32H7 ADC supports offset and gain calibration registers. Run the self-calibration sequence on boot; store the result.
