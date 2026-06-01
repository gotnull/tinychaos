# Tiny Chaos NUCLEO-H753ZI: build + flash over the on-board ST-LINK (Windows).
#
#   .\flash.ps1          build (if needed) and flash
#   .\flash.ps1 build    build only
#   .\flash.ps1 clean    wipe the build dir
#
# Windows counterpart of flash.sh. Needs on PATH: the Arm GNU toolchain
# (arm-none-eabi-gcc / -objcopy / -size), CMake, Ninja, and st-flash. The STM32
# VS Code extension bundles CMake + Ninja; install the Arm toolchain and the
# stlink tools separately. If the Arm toolchain is not on PATH, set $env:TC to
# its root (the folder containing bin\) and this script prepends bin\ for you.
#
# The STM32 VS Code extension can also build/flash this project with its own
# buttons; this script is the terminal/CI equivalent and is what the top-level
# `make` / `make flash` calls on Windows.
param([string]$cmd = "flash")

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

# Optional toolchain override: TC=<dir containing bin\arm-none-eabi-gcc.exe>.
if ($env:TC) { $env:PATH = "$env:TC\bin;$env:PATH" }

$buildDir = "build/Debug"
$elf = "$buildDir/tinychaos.elf"
$bin = "$buildDir/tinychaos.bin"

if ($cmd -eq "clean") {
    if (Test-Path build) { Remove-Item -Recurse -Force build }
    Write-Host "cleaned."
    exit 0
}

if (-not (Get-Command arm-none-eabi-gcc -ErrorAction SilentlyContinue)) {
    Write-Error "arm-none-eabi-gcc not found. Add the Arm GNU toolchain to PATH or set `$env:TC to its root."
    exit 1
}

# Configure once, then build.
if (-not (Test-Path "$buildDir/build.ninja")) {
    cmake -B $buildDir -G Ninja `
        -DCMAKE_TOOLCHAIN_FILE=cmake/gcc-arm-none-eabi.cmake `
        -DCMAKE_BUILD_TYPE=Debug
}
cmake --build $buildDir
arm-none-eabi-objcopy -O binary $elf $bin
arm-none-eabi-size $elf

if ($cmd -eq "build") {
    Write-Host "built: $bin"
    exit 0
}

Write-Host "=== flashing ==="
st-flash --reset write $bin 0x08000000
Write-Host "done. host: python -m tinychaos.cli --port COMx --baud 921600"
