#!/usr/bin/env bash
# tinychaos NUCLEO-H753ZI: build + flash over the on-board ST-LINK.
#
#   ./flash.sh          build (if needed) and flash
#   ./flash.sh build    build only
#   ./flash.sh clean    wipe the build dir
#
# Uses the standalone Arm toolchain + st-flash. The STM32 VS Code extension
# can also build/flash this project with its own buttons; this script is the
# terminal equivalent.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$HERE"

# Toolchain on PATH (official Arm GNU toolchain extracted under ~/toolchains).
TC="${TC:-$HOME/toolchains/arm-gnu-toolchain-14.2.rel1-darwin-arm64-arm-none-eabi}"
export PATH="$TC/bin:$PATH"
BUILD_DIR="build/Debug"
ELF="$BUILD_DIR/tinychaos.elf"
BIN="$BUILD_DIR/tinychaos.bin"

if ! command -v arm-none-eabi-gcc >/dev/null; then
  echo "error: arm-none-eabi-gcc not found. Set TC=<toolchain dir> or install it." >&2
  exit 1
fi

case "${1:-flash}" in
  clean)
    rm -rf build
    echo "cleaned."
    exit 0
    ;;
esac

# Configure once, then build.
if [ ! -f "$BUILD_DIR/build.ninja" ]; then
  cmake -B "$BUILD_DIR" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE=cmake/gcc-arm-none-eabi.cmake \
    -DCMAKE_BUILD_TYPE=Debug >/dev/null
fi
cmake --build "$BUILD_DIR"
arm-none-eabi-objcopy -O binary "$ELF" "$BIN"
arm-none-eabi-size "$ELF"

if [ "${1:-flash}" = "build" ]; then
  echo "built: $BIN"
  exit 0
fi

echo "=== flashing ==="
st-flash --reset write "$BIN" 0x08000000
echo "done. host: tools/.venv/bin/python -m tinychaos.cli --port /dev/cu.usbmodemXXXX --baud 921600"
