# tinychaos firmware

STM32 NUCLEO-H753ZI firmware for the entropy capture pipeline. Streams framed binary packets (see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8) to a macOS host over USB CDC or, for early bring-up, USART3 at 921600 baud.

## What is in this folder today

```
firmware/
  Makefile                          on-host test + CubeMX-Makefile passthrough
  Core/Inc/entropy_config.h         protocol constants and capture parameters
  Core/Inc/entropy_protocol.h       portable C protocol module header
  Core/Src/entropy_protocol.c       portable C protocol module implementation
  Core/Inc/usb_stream.h             USB CDC transmit ring buffer (HAL-dep)
  Core/Src/usb_stream.c             USB CDC implementation
  Core/Inc/serial_stream.h          USART3 DMA TX fallback (HAL-dep)
  Core/Src/serial_stream.c          USART3 DMA TX implementation
  Core/Src/main_skeleton.c          integration reference for main.c
  test/test_protocol_host.c         on-host self-test for entropy_protocol
```

The CubeMX-generated files (tinychaos.ioc, the CubeMX-generated Makefile, the linker script, the USB middleware, etc.) are NOT in the repo yet. The instructions below tell you how to generate them.

## Quickest path: on-host self-test

Verifies the C protocol implementation matches the Python reference byte-for-byte. No STM32 toolchain required.

```
cd firmware
make test
```

Expected output ends with:

```
encoded packet [24 bytes]:
  DA 7A 01 00 44 33 22 11 88 77 66 55 04 00 02 01
  04 03 06 05 08 07 92 4D
all checks passed
```

The hex string above is also produced bit-identical by the Python encoder for the same input. The CI/parity check is in `tools/tests/test_protocol.py::test_explicit_byte_layout`.

## Building the real firmware

Prerequisites on macOS:

```
brew install --cask stm32cubemx
brew install arm-none-eabi-gcc
brew install stlink                   # st-flash for the flashing target
```

Steps:

1. Open STM32CubeMX. File > New Project. Pick the NUCLEO-H753ZI board. Accept the default peripherals when prompted.

2. Configure the peripherals (matches `entropy_config.h`):

   - **Clock tree**: SYSCLK at 480 MHz from HSE 8 MHz crystal via PLL1. USB OTG clock 48 MHz from HSI48.
   - **ADC1**: scan mode, two channels IN0 (PA0) and IN3 (PA3), 12-bit, right-aligned, hardware-triggered by TIM2 update, sample time 16.5 cycles, DMA continuous in circular double-buffer mode to a 1024-sample buffer in DTCM.
   - **TIM2**: 1 MHz time base, period set so TIM2 update fires at 10 kHz (10 000 samples/sec per channel before scan factor). Update event is the ADC trigger source.
   - **TIM5**: 1 MHz free-running 32-bit counter. Used by main.c for `time_us`.
   - **TIM6**: 1 kHz periodic timer for the counter-packet bring-up mode (step 5). Disable after step 6.
   - **USART3**: 921600 baud, 8N1, DMA TX on stream 3. Used both as ST-LINK virtual COM (default LD2/LD3 PG7/PG6 routing) and as the UART fallback transport.
   - **USB_OTG_FS**: device mode, Middleware > USB Device > Communication Device Class (Virtual Port Com).
   - **GPIOs**: PB0 output (DMA half-complete debug toggle), PB14 keeps the NUCLEO blue LED1 use.

3. In the Project Manager tab:

   - Set Toolchain/IDE to `Makefile`.
   - Set the project name to `tinychaos`.
   - Set the project location to the parent of this `firmware/` folder.

4. Click "Generate Code". CubeMX writes a tree under `firmware/`. Move the generated Makefile to `firmware/Makefile.cube` so the top-level `Makefile` includes it:

   ```
   mv firmware/Makefile firmware/Makefile.cube
   ```

5. Apply the integration edits described in `Core/Src/main_skeleton.c`. Specifically:

   - In CubeMX-generated `Core/Src/main.c`, paste the integration blocks shown in `main_skeleton.c` into the matching `USER CODE BEGIN/END` markers. Do NOT replace the whole file.
   - In `USB_DEVICE/App/usbd_cdc_if.c`, inside `CDC_TransmitCplt_FS`, paste:
     ```
     extern void usb_stream_on_tx_complete(void);
     usb_stream_on_tx_complete();
     ```
   - In `Core/Src/main.c` (or wherever the HAL UART callback lives), inside `HAL_UART_TxCpltCallback`, paste:
     ```
     extern void serial_stream_on_tx_complete(void);
     if (huart->Instance == USART3) serial_stream_on_tx_complete();
     ```

6. Pick a transport. Edit `firmware/Core/Inc/entropy_config.h` and uncomment exactly one of:

   ```
   #define ENTROPY_TRANSPORT_USB
   #define ENTROPY_TRANSPORT_UART
   ```

7. Build:

   ```
   cd firmware
   make
   ```

   The resulting ELF/HEX/BIN are in `build/`.

8. Flash via the on-board ST-LINK:

   ```
   make flash
   ```

   (The CubeMX-generated Makefile typically provides this target; if it does not, run `st-flash write build/tinychaos.bin 0x08000000` directly.)

## Verifying the link with the host

After flashing the counter-pattern build (step 5), with the board plugged into your macOS host:

```
ls /dev/tty.usbmodem*
cd ../tools
source .venv/bin/activate
python -m tinychaos.cli --port /dev/tty.usbmodemXXXX --duration 30 --csv /tmp/counter.csv
```

You should see a summary showing tens of thousands of packets, zero bad CRCs, zero drops, and a STM32-derived rate of around 10 000 Hz. The CSV will contain a clean 12-bit counter pattern.

Once that passes, switch off `ENTROPY_USE_COUNTER_PATTERN` in `main_skeleton.c` and rebuild; the firmware now feeds real ADC samples through the same packet path.

## Where the design decisions live

- Packet sizing, transport choice, CRC variant: [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md).
- Analogue signal chain, op-amps, bias network: [../docs/hardware-design.md](../docs/hardware-design.md).
- ADC clamp and protection network: [../docs/adc-protection.md](../docs/adc-protection.md).
- Decoupling, ferrites, RC filters: [../docs/filtering-and-power.md](../docs/filtering-and-power.md).
