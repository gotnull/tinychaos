# tinychaos firmware

STM32 NUCLEO-H753ZI firmware for the entropy capture pipeline. Streams framed binary packets (see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8) to a host over USB CDC or, for early bring-up, USART3 at 921600 baud.

> **Already have a working STM32 firmware that streams raw ADC samples?** You almost certainly do not want to start over. Read [../docs/FIRMWARE_INTEGRATION.md](../docs/FIRMWARE_INTEGRATION.md). It explains exactly which three files to copy from this directory into your existing CubeMX project and the one function call you need to add inside each DMA callback. This `firmware/` directory is mostly scaffolding for the from-scratch path; the integration guide is the practical bring-existing-firmware path.

## What is in this folder today

Honest inventory. Most of this is portable scaffolding, not a flashable binary.

```
firmware/
  Makefile                          on-host test + CubeMX-Makefile passthrough
  Core/Inc/entropy_config.h         protocol constants and capture parameters       (portable C)
  Core/Inc/entropy_protocol.h       portable C protocol module header               (portable C)
  Core/Src/entropy_protocol.c       portable C protocol module implementation       (portable C, builds host-side)
  Core/Inc/usb_stream.h             USB CDC transmit ring buffer (HAL-dep)          (needs CubeMX USB middleware)
  Core/Src/usb_stream.c             USB CDC implementation                          (needs CubeMX USB middleware)
  Core/Inc/serial_stream.h          USART3 DMA TX fallback (HAL-dep)                (needs CubeMX HAL UART)
  Core/Src/serial_stream.c          USART3 DMA TX implementation                    (needs CubeMX HAL UART)
  Core/Src/main_skeleton.c          integration reference for main.c                (documentation; not meant to compile alone)
  test/test_protocol_host.c         on-host self-test for entropy_protocol          (builds with host gcc/clang)
```

Marked **portable C** above means the file compiles cleanly with both `arm-none-eabi-gcc` (for the STM32) and host `gcc` / `clang` (for the self-test). Those are the three files you copy into an existing firmware project per [../docs/FIRMWARE_INTEGRATION.md](../docs/FIRMWARE_INTEGRATION.md).

The CubeMX-generated files (`tinychaos.ioc`, the CubeMX-generated Makefile, the linker script, the USB middleware, `adc_capture.c`, etc.) are **not** in the repo. The instructions below tell you how to generate them if you are starting from scratch.

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

The firmware build is fully cross-platform: same CubeMX-generated project, same `arm-none-eabi-gcc` toolchain, same Makefile, same `st-flash` flashing utility. Only the installer commands change per OS.

### Prerequisites

**macOS** (Homebrew):

```
brew install --cask stm32cubemx
brew install arm-none-eabi-gcc
brew install stlink
```

`make` is already on macOS by default (ships with the Command Line Tools).

**Windows**:

- STM32CubeMX: download the Windows installer from [st.com/en/development-tools/stm32cubemx.html](https://www.st.com/en/development-tools/stm32cubemx.html).
- `arm-none-eabi-gcc`: download the **Arm GNU Toolchain** Windows installer from [developer.arm.com](https://developer.arm.com/downloads/-/arm-gnu-toolchain-downloads), or via Chocolatey: `choco install gcc-arm-embedded`. Confirm `arm-none-eabi-gcc --version` works in a fresh PowerShell window.
- `st-flash` and ST-LINK USB drivers: download the [ST-LINK Server](https://www.st.com/en/development-tools/stsw-link007.html) and [STM32CubeProgrammer](https://www.st.com/en/development-tools/stm32cubeprogrammer.html) from ST. Alternatively, via Chocolatey: `choco install stlink`.
- `make`: Windows does not ship with `make`. Three good options, pick one:
  1. **GNU Make for Windows** standalone binary: `choco install make`.
  2. **MSYS2 / MinGW64**: install MSYS2 from [msys2.org](https://www.msys2.org/), then `pacman -S make`.
  3. **WSL (Windows Subsystem for Linux)**: gives you a real Linux environment with `apt install gcc-arm-none-eabi make stlink-tools`. The build runs unchanged; flashing requires ST-LINK USB device passthrough from Windows.

**Linux** (Debian/Ubuntu):

```
sudo apt install gcc-arm-none-eabi stlink-tools make
```

For other distros: install equivalent packages (often called `arm-none-eabi-gcc-newlib` or similar), plus `stlink` from the [stlink-org repository](https://github.com/stlink-org/stlink).

### Steps

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

After flashing the counter-pattern build (step 5), with the board plugged into the host:

```
cd ../tools
# activate the venv first (see ../tools/README.md for the OS-specific command)

# substitute the real serial-port name for your OS:
#   Windows:  COM4
#   macOS:    /dev/tty.usbmodemXXXXX
#   Linux:    /dev/ttyACM0
python -m tinychaos.cli --port <PORT> --duration 30 --csv counter.csv
```

You should see a summary showing tens of thousands of packets, zero bad CRCs, zero drops, and a STM32-derived rate of around 10 000 Hz. The CSV will contain a clean 12-bit counter pattern.

Once that passes, switch off `ENTROPY_USE_COUNTER_PATTERN` in `main_skeleton.c` and rebuild; the firmware now feeds real ADC samples through the same packet path.

## Where the design decisions live

- Packet sizing, transport choice, CRC variant: [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md).
- Analogue signal chain, op-amps, bias network: [../docs/hardware-design.md](../docs/hardware-design.md).
- ADC clamp and protection network: [../docs/adc-protection.md](../docs/adc-protection.md).
- Decoupling, ferrites, RC filters: [../docs/filtering-and-power.md](../docs/filtering-and-power.md).
