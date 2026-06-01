# Tiny Chaos firmware Makefile (top level)
#
# Two roles:
#
#   1. `make test`  - portable on-host self-test of the entropy_protocol C
#      module. Uses the system gcc/clang; does NOT need the Arm toolchain.
#      Useful for CI and for verifying CRC parity with the Python reference.
#
#   2. `make` / `make all` / `make flash` / `make clean`  - build and flash the
#      STM32 NUCLEO-H753ZI firmware. These delegate to the real CMake project in
#      nucleo-h753zi/ via its per-platform helper script - flash.sh on
#      macOS/Linux, flash.ps1 on Windows - which knows where the Arm toolchain
#      lives and drives CMake + Ninja + st-flash.
#
# The firmware itself is the CMake project under nucleo-h753zi/ (generated from
# tinychaos.ioc). The old "generate a Makefile.cube from CubeMX" flow is gone;
# this top-level Makefile is a thin cross-platform front end so editor "build /
# flash" tasks that run `make` here keep working on both Windows and macOS.

NUCLEO := nucleo-h753zi

# Per-platform invocation of the build/flash helper. GNU make sets OS to
# "Windows_NT" on Windows (cmd or MSYS); everything else is treated as Unix.
ifeq ($(OS),Windows_NT)
  HELPER = cd $(NUCLEO) && powershell -NoProfile -ExecutionPolicy Bypass -File flash.ps1
else
  HELPER = cd $(NUCLEO) && ./flash.sh
endif

# `make` with no target builds the firmware (does not flash).
.DEFAULT_GOAL := all

.PHONY: all flash clean test clean-test

## Build the STM32 firmware (no flashing).
all:
	$(HELPER) build

## Build (if needed) and flash over the on-board ST-LINK.
flash:
	$(HELPER)

## Remove both the firmware build dir and the host-test build dir.
clean:
	$(HELPER) clean
	-rm -rf $(TEST_BUILD)

# ---- On-host self-test target -----------------------------------------
#
# Independent of the Arm toolchain: compiles the portable C protocol module
# with the system compiler and checks its CRC / byte layout. `make CC=clang
# test` overrides the compiler.

CC ?= gcc
CFLAGS_TEST ?= -std=c99 -Wall -Wextra -Werror -O2 -ICore/Inc

TEST_BUILD := build-test
TEST_BIN := $(TEST_BUILD)/test_protocol_host
TEST_SRC := test/test_protocol_host.c Core/Src/entropy_protocol.c

test: $(TEST_BIN)
	@echo "running $(TEST_BIN)"
	@$(TEST_BIN)

$(TEST_BIN): $(TEST_SRC) Core/Inc/entropy_protocol.h Core/Inc/entropy_config.h
	@mkdir -p $(TEST_BUILD)
	$(CC) $(CFLAGS_TEST) $(TEST_SRC) -o $(TEST_BIN)

clean-test:
	rm -rf $(TEST_BUILD)
