# tinychaos firmware Makefile (top level)
#
# This Makefile has two roles:
#
#   1. ``make test``  builds and runs the portable on-host self-test for
#      the entropy_protocol module. Uses the system gcc/clang. Does NOT
#      require arm-none-eabi-gcc. Useful for CI and for verifying CRC
#      parity with the Python reference implementation.
#
#   2. ``make`` / ``make all`` / ``make flash``  builds and flashes the
#      firmware for the STM32 NUCLEO-H753ZI. Requires:
#         - CubeMX-generated project files (run CubeMX from tinychaos.ioc).
#         - arm-none-eabi-gcc toolchain on PATH.
#         - st-flash or openocd for flashing.
#
# CubeMX generates its own GNU Makefile when you choose "Makefile" as the
# toolchain in the project settings. The convention in this project is to
# regenerate that Makefile via CubeMX and then commit it alongside the
# updated .ioc file. The test target below is independent and lives at
# the top level so it works even when the CubeMX skeleton has not yet
# been generated.

# ---- On-host self-test target -----------------------------------------

CC ?= gcc
CFLAGS_TEST ?= -std=c99 -Wall -Wextra -Werror -O2 -ICore/Inc
# `gcc` is the most portable default: macOS (Xcode CLT aliases gcc to clang),
# Linux (gcc is native), Windows (MSYS2/MinGW provides gcc). The CC variable
# can be overridden on the command line: `make CC=clang test`.

TEST_BUILD := build-test
TEST_BIN := $(TEST_BUILD)/test_protocol_host
TEST_SRC := test/test_protocol_host.c Core/Src/entropy_protocol.c

.PHONY: test
test: $(TEST_BIN)
	@echo "running $(TEST_BIN)"
	@$(TEST_BIN)

$(TEST_BIN): $(TEST_SRC) Core/Inc/entropy_protocol.h Core/Inc/entropy_config.h
	@mkdir -p $(TEST_BUILD)
	$(CC) $(CFLAGS_TEST) $(TEST_SRC) -o $(TEST_BIN)

.PHONY: clean-test
clean-test:
	rm -rf $(TEST_BUILD)

# ---- STM32 firmware build (CubeMX-generated) --------------------------
#
# The CubeMX-generated Makefile lives at firmware/Makefile.cube and is
# included here only when the file exists. Before generation, ``make all``
# falls back to the test target so a fresh checkout still has something
# useful to run.

ifneq (,$(wildcard Makefile.cube))
include Makefile.cube
else
.PHONY: all
all: test
	@echo
	@echo "note: firmware build skeleton not yet generated."
	@echo "run STM32CubeMX on tinychaos.ioc, choose Makefile as the toolchain,"
	@echo "and save the generated Makefile as Makefile.cube in this directory."
endif

.PHONY: clean
clean: clean-test
	-rm -rf build
