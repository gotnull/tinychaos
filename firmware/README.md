# tinychaos firmware

STM32 NUCLEO-H753ZI firmware for the entropy capture pipeline. Streams framed binary packets (see [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8) to a host over USART3 (default, on the on-board ST-LINK VCP) or native USB CDC on CN13.

## Build and flash the firmware (it is already in the repo)

**You do not need to run STM32CubeMX, and there is nothing to "generate".** A complete, committed, buildable CubeMX project lives in [nucleo-h753zi/](nucleo-h753zi/) - the CMake build, the linker script, the HAL drivers, the `.ioc`, and the full application ([nucleo-h753zi/Core/Src/entropy_app.c](nucleo-h753zi/Core/Src/entropy_app.c)) are all here. Build and flash it over the on-board ST-LINK:

```
cd firmware/nucleo-h753zi
./flash.sh          # macOS / Linux:  build + flash
.\flash.ps1         # Windows (PowerShell):  build + flash
```

Needs on PATH: the Arm GNU toolchain (`arm-none-eabi-gcc`), CMake, Ninja, and `st-flash` (see [Prerequisites](#prerequisites) below for the per-OS install). The STM32 VS Code extension can also build/flash this project with its own buttons.

- **Transport** is a compile-time choice (UART vs USB CDC) - see [Transport (UART vs USB CDC)](#transport-uart-vs-usb-cdc) below.
- The capture is **two-channel**: ADC1 rank 1 = **PA3 / ADC1_INP15** (A0, the zener entropy source), rank 2 = **PC0 / ADC1_INP10** (A1, the baseline-divider reference), paced by **TIM3**. The host de-interleaves them as channel 0 / channel 1.
- To regenerate the project from scratch (you almost never need to), see [NUCLEO_SETUP.md](NUCLEO_SETUP.md).

> **Already have a *different* working STM32 firmware that streams raw ADC samples?** You can keep it. Read [../docs/FIRMWARE_INTEGRATION.md](../docs/FIRMWARE_INTEGRATION.md): it explains which three portable C files to copy into your existing CubeMX project and the one function call to add inside each DMA callback, so you get the whole host pipeline without porting onto our project.

## What is in this folder

```
firmware/
  nucleo-h753zi/                    >>> the complete, buildable STM32 project <<<
    tinychaos.ioc                   CubeMX project (committed)
    CMakeLists.txt, CMakePresets    CMake + Ninja build
    STM32H753XX_FLASH.ld            linker script
    flash.sh / flash.ps1            build + flash over the on-board ST-LINK
    Core/Src/entropy_app.c          the application: TIM3 -> ADC1 (PA3 + PC0) -> DMA -> framed packets
    Core/, Drivers/, Middlewares/   CubeMX-generated HAL + USB device stack
  Core/Inc/entropy_config.h         protocol constants and capture parameters       (portable C, shared)
  Core/Inc/entropy_protocol.h       portable C protocol module header               (portable C, shared)
  Core/Src/entropy_protocol.c       portable C protocol module implementation       (portable C, builds host-side)
  Core/Inc|Src/usb_stream.*         USB CDC transmit ring buffer
  Core/Inc|Src/serial_stream.*      USART3 DMA TX
  Core/Src/main_skeleton.c          legacy integration reference (superseded by nucleo-h753zi/entropy_app.c)
  Makefile                          on-host protocol self-test (no STM32 toolchain needed)
  test/test_protocol_host.c         on-host self-test for entropy_protocol          (builds with host gcc/clang)
```

The portable C files (`entropy_protocol.*`, `entropy_config.h`, `usb_stream.*`, `serial_stream.*`) compile cleanly with both `arm-none-eabi-gcc` (for the STM32) and host `gcc` / `clang` (for the self-test). The `nucleo-h753zi/` project consumes them via its include path; they are also the files you copy into a *different* existing firmware per [../docs/FIRMWARE_INTEGRATION.md](../docs/FIRMWARE_INTEGRATION.md).

`main_skeleton.c` is a **legacy** copy-paste-into-`main.c` reference from before the `nucleo-h753zi/` project existed. The real integration is done for you in [nucleo-h753zi/Core/Src/entropy_app.c](nucleo-h753zi/Core/Src/entropy_app.c) (the generated `main.c` just calls `entropy_app_init()` / `entropy_app_task()`), so you do not need to paste anything.

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

The project in [nucleo-h753zi/](nucleo-h753zi/) is already generated and committed. Build it with CMake + Ninja and flash over the on-board ST-LINK - the `flash.sh` / `flash.ps1` wrappers do both:

```
cd firmware/nucleo-h753zi
./flash.sh            # macOS / Linux   (build + flash)
./flash.sh build      # build only
./flash.sh clean      # wipe the build dir
.\flash.ps1           # Windows (same subcommands)
```

You can also open `firmware/nucleo-h753zi/` in VS Code with the STM32 extension and use its Build / Flash buttons, or drag-drop the built `build/Debug/tinychaos.bin` onto the `NODE_H753ZI` USB drive the ST-LINK presents.

### Prerequisites

The build needs the **Arm GNU toolchain** (`arm-none-eabi-gcc`), **CMake**, **Ninja**, and **`st-flash`** on PATH. (No `make`, no `stm32cubemx` - those were only for the old from-scratch path.)

**macOS** (Homebrew):

```
brew install arm-none-eabi-gcc cmake ninja stlink
```

**Windows**:

- `arm-none-eabi-gcc`: the **Arm GNU Toolchain** installer from [developer.arm.com](https://developer.arm.com/downloads/-/arm-gnu-toolchain-downloads), or `choco install gcc-arm-embedded`. Confirm `arm-none-eabi-gcc --version` in a fresh PowerShell. (If it is not on PATH, set `$env:TC` to its root and `flash.ps1` prepends `bin\` for you.)
- CMake + Ninja: `choco install cmake ninja` (the STM32 VS Code extension also bundles these).
- `st-flash` + ST-LINK drivers: `choco install stlink`, or [STM32CubeProgrammer](https://www.st.com/en/development-tools/stm32cubeprogrammer.html) from ST.

**Linux** (Debian/Ubuntu):

```
sudo apt install gcc-arm-none-eabi cmake ninja-build stlink-tools
```

### Regenerating from scratch

You almost never need this - the project is committed. If you ever do need to regenerate the HAL/clock/USB tree from the `.ioc` (or rebuild it on a fresh board), the full CubeMX recipe and the two `main.c` hook calls are in [NUCLEO_SETUP.md](NUCLEO_SETUP.md). All of our logic lives in `entropy_app.c`, which survives regeneration, so regen only ever touches the generated HAL - never our code.

## Transport (UART vs USB CDC)

The capture is framed identically regardless of how the bytes leave the chip, so
the host (GUI/CLI) decodes either transport with **zero changes** - it just opens
a serial port. The transport is a **compile-time choice** in the
`nucleo-h753zi/` CMake project, selected with `-DENTROPY_TRANSPORT`:

| | `UART` (default) | `USB` (CDC) |
|---|---|---|
| Port | USART3 on the ST-LINK VCP | native USB on **CN13** (`USB_OTG_FS`) |
| Cables | one (CN1: power + flash + data) | two (CN1 power+flash, **CN13** data) |
| Host sees | the ST-LINK `usbmodem`/`COM` | a **new** `usbmodem`/`COM` (the CDC port) |
| Throughput | ~92 kB/s @ 921600 | ~1 MB/s |
| Setup | nothing - it's the tested default | needs the USB device stack (below) |

**Build UART (default):**
```
cd nucleo-h753zi && ./flash.sh          # mac/linux   (flash.ps1 on Windows)
```

**Build USB CDC:**
```
cmake -B build/Debug -G Ninja -DENTROPY_TRANSPORT=USB \
      -DCMAKE_TOOLCHAIN_FILE=cmake/gcc-arm-none-eabi.cmake -DCMAKE_BUILD_TYPE=Debug
cmake --build build/Debug
```
The USB build needs the **CubeMX-generated USB device stack** added to the
project (it is not in the barebones `.ioc`). One-time setup:

1. Open `tinychaos.ioc` in CubeMX, enable **USB_OTG_FS = Device_Only** plus the
   **USB Device / Communication Device Class (CDC)** middleware, set the USB
   48 MHz clock, and regenerate.
2. Add the generated sources + include dirs to `CMakeLists.txt` (there's a
   `>>> ADD YOUR ... USB DEVICE STACK HERE <<<` marker in the `USB` branch
   listing exactly which files: `usb_device.c`, `usbd_desc.c`, `usbd_cdc_if.c`,
   `usbd_conf.c`, and the USB Device Library core + `usbd_cdc.c`).
3. Add `OTG_FS_IRQHandler` (calls `HAL_PCD_IRQHandler`) and make
   `CDC_TransmitCplt_FS` (in `usbd_cdc_if.c`) call `usb_stream_on_tx_complete()`.

`entropy_app.c` already handles the rest: when `ENTROPY_TRANSPORT_USB` is
defined it calls `MX_USB_DEVICE_Init()` and sends via `usb_stream_send()`
instead of the USART3 path - the ADC/TIM/DMA capture is identical either way.

Wiring for the USB build (NUCLEO-H753ZI): power + flash over **CN1** (ST-LINK)
as usual, and run a **second** USB cable from **CN13** to the host for the CDC
link. CN13 is data-only (it does not power the board). Ensure the USB jumper
(UM: JP4) is set, or `vbus_sensing_enable = DISABLE` in the USB config.

## Verifying the link with the host

After flashing (`./flash.sh`), with the board plugged into the host:

```
cd ../tools
# activate the venv first (see ../tools/README.md for the OS-specific command)

# substitute the real serial-port name for your OS:
#   Windows:  COM4
#   macOS:    /dev/tty.usbmodemXXXXX
#   Linux:    /dev/ttyACM0
python -m tinychaos.cli --port <PORT> --duration 30 --csv counter.csv
```

You should see a summary showing tens of thousands of packets, zero bad CRCs, zero drops, and a STM32-derived rate around the configured `ADC_SAMPLE_RATE_HZ`. The two channels (PA3 zener, PC0 baseline) alternate in `channel_index` 0 and 1; with no analogue front-end wired they sit near the ADC noise floor, and the zener channel comes alive once the breadboard chain (see [../docs/hardware-design.md](../docs/hardware-design.md)) is connected.

## Where the design decisions live

- Packet sizing, transport choice, CRC variant: [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md).
- Analogue signal chain, op-amps, bias network: [../docs/hardware-design.md](../docs/hardware-design.md).
- ADC clamp and protection network: [../docs/adc-protection.md](../docs/adc-protection.md).
- Decoupling, ferrites, RC filters: [../docs/filtering-and-power.md](../docs/filtering-and-power.md).

## Credits

The original concept for Tiny Chaos came from **The Don**.
