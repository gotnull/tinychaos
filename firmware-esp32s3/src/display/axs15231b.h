#pragma once

#include <Arduino.h>

void axs15231bInit();
void axs15231bSetBacklight(bool on);
void axs15231bSetBrightnessPercent(uint8_t percent);
void axs15231bSleep();
void axs15231bWake();
void axs15231bPushColors(uint16_t x, uint16_t y, uint16_t width, uint16_t height,
                         const uint16_t *data);
// Async push — queues SPI transactions via spi_device_queue_trans and returns
// immediately. The caller MUST keep `data` alive until axs15231bPushColorsWait()
// returns. Use a double-buffer pattern (compose into buffer N+1 while DMA
// sends buffer N) to overlap CPU compose with the ~11.5 ms SPI floor.
void axs15231bPushColorsBegin(uint16_t x, uint16_t y, uint16_t width, uint16_t height,
                              const uint16_t *data);
// Drains every pending transaction queued by Begin. Yields to other FreeRTOS
// tasks during the wait (semaphore-blocked, not busy-polled).
void axs15231bPushColorsWait();
