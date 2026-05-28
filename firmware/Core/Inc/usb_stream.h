/*
 * usb_stream.h - USB CDC transmit ring buffer
 *
 * Sits between the ADC capture callback (producer) and the CubeMX-generated
 * CDC TX path (CDC_Transmit_FS). Provides a small bounded ring buffer of
 * pending packets so the producer is never blocked.
 *
 * Producer behaviour: usb_stream_send returns true if the packet was
 * enqueued, false if the ring was full. The producer should treat a false
 * return as a counted drop and continue. Whole missing packets are
 * detectable by the host via SEQ gaps.
 *
 * Consumer behaviour: the USB IN-endpoint completion callback
 * (CDC_TransmitCplt_FS, weakly defined in usbd_cdc_if.c) calls
 * usb_stream_pump to dequeue and submit the next packet to the USB stack.
 * The main loop should also call usb_stream_pump periodically to bootstrap
 * the first send and to recover if the completion callback is missed.
 */

#ifndef TINYCHAOS_USB_STREAM_H
#define TINYCHAOS_USB_STREAM_H

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
    uint32_t dropped;     /* full-ring drops by producer */
    uint32_t tx_failed;   /* CDC_Transmit_FS returned non-OK */
    uint32_t queue_depth; /* current pending count */
} usb_stream_stats_t;

/* Initialise the ring buffer state. Call once from main, after the USB
 * stack has been initialised by the CubeMX-generated MX_USB_DEVICE_Init.
 */
void usb_stream_init(void);

/* Enqueue a packet for transmission. Returns true if the packet was queued,
 * false if the ring buffer was full.
 *
 * Copies ``len`` bytes from ``data`` into an internal slot, so the caller's
 * buffer can be reused immediately on return.
 */
bool usb_stream_send(const uint8_t *data, size_t len);

/* Pump the queue: if no transmission is in flight, dequeue the next pending
 * packet and submit it to CDC_Transmit_FS. Idempotent and cheap.
 *
 * Called from CDC_TransmitCplt_FS (the USB IN-EP completion) and from the
 * main loop.
 */
void usb_stream_pump(void);

/* Get a snapshot of the stats counters. */
usb_stream_stats_t usb_stream_get_stats(void);

#ifdef __cplusplus
}
#endif

#endif /* TINYCHAOS_USB_STREAM_H */
