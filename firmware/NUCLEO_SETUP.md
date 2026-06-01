# NUCLEO-H753ZI bring-up

This is the exact recipe to get framed packets streaming from the NUCLEO to
the host tools. The one step that needs a GUI is CubeMX generating the
HAL/USB/clock project — that's an STM32 reality, not optional. See
[main_skeleton.c](Core/Src/main_skeleton.c) for the exact code blocks to paste.

## Build environment — pick one

**A. STM32 VS Code extension (installed — recommended).** Builds with **CMake +
Ninja** using a toolchain + flasher it downloads itself via its bundles-manager.
You still need **CubeMX** for codegen and **STM32CubeCLT** for the
toolchain/programmer — the extension will prompt to install both (Command
Palette: `STM32: Manage ... bundles` / it offers them on first project open).
With this path, our modules go in **`CMakeLists.txt`**, and you build/flash with
the extension's buttons. In CubeMX's Project Manager choose **Toolchain/IDE =
CMake**.

**B. Plain CLI (already set up as a fallback).** A complete Arm toolchain is
extracted at `~/toolchains/arm-gnu-toolchain-14.2.rel1-darwin-arm64-arm-none-eabi`
(verified: compiles+links for the M7 with newlib), and `st-flash` is installed.
With this path choose **Toolchain/IDE = Makefile** in CubeMX, our modules go in
the **Makefile** `C_SOURCES`, and you `make` + `st-flash` from the terminal.

The CubeMX peripheral config and the USER CODE hooks below are **identical** for
both paths — only "how you add our 3 source files" and "how you build/flash"
differ (called out in steps 2a and 3).

Bring-up order is deliberate — prove each layer before adding the next:

1. **UART over the ST-LINK VCP** (zero friction: same cable you flash with, no
   USB-CDC, no VBUS, no CN13 solder bridges) + **counter pattern** (no ADC).
   Proves clock + protocol + host decode.
2. **USB CDC** over CN13 + counter pattern. Proves the USB transport.
3. **Real ADC** + DMA. Proves the capture chain.

Do them in that order and any failure is isolated to the layer you just added.

---

## 0. Get CubeMX

CubeMX is not on Homebrew (ST gates the download behind a free account). Two
options:

- **Download it** from st.com (search "STM32CubeMX", make a free ST account).
  macOS `.app`; no admin needed to run.
- **Or skip it entirely** by starting from a project already generated for this
  exact board (e.g. the existing working CDC project on the same NUCLEO) and
  just dropping our modules in — jump to step 3.

---

## 1. CubeMX project configuration

New Project -> Board Selector -> **NUCLEO-H753ZI** -> "Yes" to initialise
default peripherals (this sets up LD1/2/3, B1, and USART3 on the ST-LINK VCP).

### Clock (Clock Configuration tab)
- Input: HSE = **8 MHz** (ST-LINK MCO).
- Target **SYSCLK = 400 MHz** (HCLK 200 MHz). Use "Resolve Clock Issues" if it
  complains; 400 is safe and standard for the H753.
- **USB 48 MHz clock**: enable **HSI48** (RCC -> "HSI48" / RCC -> CRS, or set
  the USB clock mux to HSI48). CubeMX will flag the USB clock until this is
  set to exactly 48 MHz.

### USB (only needed for step 2+, skip for the UART smoke test)
- `Connectivity -> USB_OTG_FS -> Mode = Device_Only`.
- `Middleware -> USB_DEVICE -> Class For FS IP = Communication Device Class
  (Virtual Port Com)`.
- **CRITICAL:** `USB_OTG_FS -> Parameters -> Activate VBUS sensing = DISABLED`.
  (Powered from ST-LINK there is no VBUS to sense; leave it on and it never
  enumerates — this is the classic "dead port" trap.)
- PA11 (USB_DM) / PA12 (USB_DP) auto-assign. On the H753ZI these reach CN13;
  if nothing enumerates, check solder bridges SB by the CN13/USB pins.

### Timers
- **TIM5** (32-bit) — free-running microsecond clock for the packet `time_us`
  field. Clock Source = Internal. Set Prescaler so the counter ticks at
  **1 MHz** (`prescaler = (timer_clk_hz / 1_000_000) - 1`), Period = `0xFFFFFFFF`.
  No interrupt needed.
- **TIM6** — packet emit tick for the counter-pattern bring-up. Internal clock,
  set Prescaler+Period for whatever packet rate you want (e.g. ~78 Hz to mimic
  the 20 kSa/s @ 256 design, or slower for first light). **Enable its global
  interrupt** (NVIC) so `HAL_TIM_PeriodElapsedCallback` fires.

### ADC (step 3 only — leave out for first light)
- `ADC1 -> IN<x>` on a free pin, 12-bit, **Continuous Conversion**, **DMA**
  (circular), triggered by a timer at `ADC_SAMPLE_RATE_HZ`. DMA buffer sized to
  `2 * PACKET_SAMPLE_COUNT` so each half-callback is exactly one packet.
  (Details when we get here — the counter pattern proves everything else first.)

### Project Manager tab
- **Toolchain / IDE**: `CMake` for the VS Code extension (path A), or `Makefile`
  for the CLI path (path B).
- Project name + location: generate it **outside** this repo (or into
  `firmware/cube/`) so the generated tree stays separate from our modules.
- Generate Code.

---

## 2. Drop in our modules + wire the hooks

After generation you have a `Core/`, `Drivers/`, `USB_DEVICE/` (if CDC) and a
`Makefile`. Now integrate:

### a) Add our source + headers
Copy (or symlink) these into the generated project's `Core/`:
- `Core/Src/entropy_protocol.c`, `Core/Inc/entropy_protocol.h`
- `Core/Inc/entropy_config.h`
- USB path: `Core/Src/usb_stream.c`, `Core/Inc/usb_stream.h`
- UART path: `Core/Src/serial_stream.c`, `Core/Inc/serial_stream.h`

Then register the new `.c` files with the build:
- **CMake (path A):** in the project's `CMakeLists.txt`, inside a
  `# USER CODE` / user-sources block, add them to the app target:
  ```cmake
  target_sources(${CMAKE_PROJECT_NAME} PRIVATE
      Core/Src/entropy_protocol.c
      Core/Src/usb_stream.c          # or serial_stream.c for UART
  )
  target_include_directories(${CMAKE_PROJECT_NAME} PRIVATE Core/Inc)
  ```
  (CubeMX's CMake output often globs `Core/Src/*.c` already — if so they're
  picked up automatically; just confirm `Core/Inc` is on the include path.)
- **Makefile (path B):** add the `.c` files to `C_SOURCES` (Core/Src is usually
  globbed — confirm) and ensure `-ICore/Inc` is in `C_INCLUDES` (default).

### b) Pick the transport
Define **one** transport symbol. Simplest cross-build-system way: uncomment the
matching `#define` in `entropy_config.h`:
```c
/* #define ENTROPY_TRANSPORT_UART */   /* step 1 (ST-LINK VCP) */
/* #define ENTROPY_TRANSPORT_USB  */   /* step 2+ (CDC over CN13) */
```
(Or add it as a compile define: CMake `add_compile_definitions(ENTROPY_TRANSPORT_USB)`
/ Makefile `C_DEFS += -DENTROPY_TRANSPORT_USB`.)

### c) Paste the integration blocks
From [main_skeleton.c](Core/Src/main_skeleton.c), copy the marked blocks into
the **matching `USER CODE BEGIN/END`** regions of the generated `Core/Src/main.c`
(never replace the whole file — CubeMX rewrites it on regen, but preserves
USER CODE regions):
- the includes + transport `#define` aliases,
- `s_seq` / `s_pkt` statics + `emit_counter_packet()` + `HAL_TIM_PeriodElapsedCallback`,
- in `USER CODE BEGIN 2`: `stream_init();`, the `entropy_protocol_self_test()`
  check, `HAL_TIM_Base_Start(&htim5);`, `HAL_TIM_Base_Start_IT(&htim6);`,
- in `USER CODE BEGIN WHILE`: `stream_pump();` + an LD1 toggle heartbeat.

### d) Wire the transmit-complete callback (the one your cousin hit)
- **USB:** in `USB_DEVICE/App/usbd_cdc_if.c`, inside `CDC_TransmitCplt_FS(...)`,
  in a `USER CODE` block:
  ```c
  /* USER CODE BEGIN N */
  #include "usb_stream.h"          /* put in the file's USER CODE INCLUDE block */
  usb_stream_on_tx_complete();
  /* USER CODE END N */
  ```
- **UART:** in `HAL_UART_TxCpltCallback` (stm32h7xx_it.c or main.c):
  ```c
  if (huart->Instance == USART3) serial_stream_on_tx_complete();
  ```

---

## 3. Build + flash

**Path A (VS Code extension):** use the extension's **Build** and **Run/Flash**
buttons (status bar / Command Palette `STM32: Build`, `STM32: Flash`). It drives
CMake+Ninja and the ST-LINK programmer for you. Debug works via the bundled
ST-LINK gdbserver.

**Path B (CLI):**
```sh
TC=~/toolchains/arm-gnu-toolchain-14.2.rel1-darwin-arm64-arm-none-eabi
make GCC_PATH=$TC/bin          # -> build/<name>.bin / .elf
st-flash --reset write build/<name>.bin 0x08000000
```
(CubeMX's Makefile takes `GCC_PATH` to point at the toolchain `bin`.)

**Either path:** you can always just **drag-drop** `build/<name>.bin` onto the
`NODE_H753ZI` mass-storage drive the onboard ST-LINK presents — simplest flash
of all, no tool needed.

---

## 4. Verify on the host

UART (step 1) — the ST-LINK VCP shows up as `/dev/cu.usbmodem*`:
```sh
cd tools && .venv/bin/python -m tinychaos.cli --port /dev/cu.usbmodemXXXX --baud 921600
```
USB CDC (step 2) — CN13 enumerates as a second `/dev/cu.usbmodem*`; same command
on that port. Either way you should see packets decode with no bad CRCs and a
clean 16-bit counter ramp (mod 4096) in the samples.

Sanity check at any point: distance between two `DA 7A` markers is
`14 + 2*PACKET_SAMPLE_COUNT + 2` bytes, dead consistent.
