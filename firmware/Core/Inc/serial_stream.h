/*
 * serial_stream.h - UART transmit fallback
 *
 * Same producer-side interface as usb_stream so main.c can swap transports
 * at compile time. Uses USART3 in DMA TX mode at 921600 baud (configurable
 * in the CubeMX project). The on-board ST-LINK exposes USART3 as a USB
 * virtual COM port, so this transport works over the same physical USB
 * cable as the USB CDC option, just through a different driver path.
 *
 * Use this for bring-up before USB CDC is wired into the project. Its
 * throughput ceiling (~115 kB/s) is comfortable for 10 kHz two-channel
 * sampling but does not scale to 100 kHz or higher.
 */

#ifndef TINYCHAOS_SERIAL_STREAM_H
#define TINYCHAOS_SERIAL_STREAM_H

#include <stdbool.h>
#include <stdint.h>
#include <stddef.h>

#include "entropy_config.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t enqueued;
    uint32_t transmitted;
    uint32_t dropped;
    uint32_t tx_failed;
    uint32_t queue_depth;
} serial_stream_stats_t;

void serial_stream_init(void);

/* Enqueue a packet for transmission. Returns true if queued, false on
 * full-ring drop. Has the same semantics as usb_stream_send.
 */
bool serial_stream_send(const uint8_t *data, size_t len);

void serial_stream_pump(void);

serial_stream_stats_t serial_stream_get_stats(void);

/* Called by the user-provided HAL_UART_TxCpltCallback in main.c (or in
 * stm32h7xx_it.c). Replace the body of the existing callback with a single
 * call to this function.
 */
void serial_stream_on_tx_complete(void);

#ifdef __cplusplus
}
#endif

#endif /* TINYCHAOS_SERIAL_STREAM_H */
