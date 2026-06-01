cmake_minimum_required(VERSION 3.22)

#
# This file is generated only once,
# and is not re-generated if converter is called multiple times.
#
# User is free to modify the file as much as necessary
#

# Setup compiler settings
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS ON)


# Define the build type
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE "Debug")
endif()

# Set the project name
set(CMAKE_PROJECT_NAME tinychaos)

# Enable compile command to ease indexing with e.g. clangd
set(CMAKE_EXPORT_COMPILE_COMMANDS TRUE)

# Core project settings
project(${CMAKE_PROJECT_NAME})
message("Build type: " ${CMAKE_BUILD_TYPE})

# Enable CMake support for ASM and C languages
enable_language(C ASM)

# Create an executable object type
add_executable(${CMAKE_PROJECT_NAME})

# Add STM32CubeMX generated sources
add_subdirectory(cmake/stm32cubemx)

# Link directories setup
target_link_directories(${CMAKE_PROJECT_NAME} PRIVATE
    # Add user defined library search paths
)

# Add sources to executable
target_sources(${CMAKE_PROJECT_NAME} PRIVATE
    # tinychaos application glue (board-specific)
    Core/Src/entropy_app.c
    # Portable wire-protocol module (single source of truth, shared with the
    # ESP32 firmware + host tools). Referenced from ../Core, not duplicated.
    ${CMAKE_CURRENT_SOURCE_DIR}/../Core/Src/entropy_protocol.c
    # Non-blocking UART TX ring (drains via DMA). Its ring buffer lives in
    # .bss, which the linker now places in AXI SRAM (DMA-reachable).
    ${CMAKE_CURRENT_SOURCE_DIR}/../Core/Src/serial_stream.c
    # USART3 + ADC1 HAL drivers: not auto-included because those peripherals
    # aren't in the .ioc (we bring them up by hand in entropy_app.c). They
    # compile under the HAL_*_MODULE_ENABLED defines below.
    ${CMAKE_CURRENT_SOURCE_DIR}/Drivers/STM32H7xx_HAL_Driver/Src/stm32h7xx_hal_uart.c
    ${CMAKE_CURRENT_SOURCE_DIR}/Drivers/STM32H7xx_HAL_Driver/Src/stm32h7xx_hal_uart_ex.c
    ${CMAKE_CURRENT_SOURCE_DIR}/Drivers/STM32H7xx_HAL_Driver/Src/stm32h7xx_hal_adc.c
    ${CMAKE_CURRENT_SOURCE_DIR}/Drivers/STM32H7xx_HAL_Driver/Src/stm32h7xx_hal_adc_ex.c
)

# Add include paths
target_include_directories(${CMAKE_PROJECT_NAME} PRIVATE
    Core/Inc
    ${CMAKE_CURRENT_SOURCE_DIR}/../Core/Inc   # entropy_config.h, entropy_protocol.h
)

# Add project symbols (macros)
target_compile_definitions(${CMAKE_PROJECT_NAME} PRIVATE
    # USART3 is brought up by hand in entropy_app.c (not via CubeMX), so the
    # HAL UART module isn't auto-enabled in stm32h7xx_hal_conf.h. Enable it
    # here so the build stays correct across CubeMX regenerations.
    HAL_UART_MODULE_ENABLED
    HAL_ADC_MODULE_ENABLED
    ENTROPY_TRANSPORT_UART
)

# Remove wrong libob.a library dependency when using cpp files
list(REMOVE_ITEM CMAKE_C_IMPLICIT_LINK_LIBRARIES ob)

# Add linked libraries
target_link_libraries(${CMAKE_PROJECT_NAME}
    stm32cubemx

    # Add user defined libraries
)
