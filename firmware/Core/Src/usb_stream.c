/*
 * usb_stream.c - USB CDC transmit ring buffer implementation
 *
 * HAL-dependent. This file references usbd_cdc_if.h from the CubeMX-
 * generated USB middleware. Build it as part of the firmware target, not
 * the host self-test.
 *
 * Strategy:
 *   - The ring stores whole packets, one per slot, sized to the maximum
 *     encoded packet length. This avoids per-byte synchronisation between
 *     producer and consumer; we just exchange whole slots.
 *   - Producer is the ADC capture callback (interrupt context).
 *   - Consumer is the USB endpoint completion callback (CDC_TransmitCplt_FS)
 *     plus a fallback poll from the main loop.
 *   - Atomicity: the head and tail indices are uint32_t and updated last,
 *     so a reader sees either the old or new value, never garbage. Single-
 *     producer single-consumer; no locks needed.
 */

#include <string.h>

#include "usb_stream.h"

/* Only compile this module when the firmware target is USB CDC. The same
 * symbol must be defined when building the firmware target (e.g. via a
 * -D flag in the CubeMX Makefile or by editing entropy_config.h).
 */
#ifdef ENTROPY_TRANSPORT_USB

#include "usbd_cdc_if.h" /* CDC_Transmit_FS, USBD_OK */

/* Number of packet slots in the ring. With PACKET_SAMPLE_COUNT = 256 and
 * the default 64-78 packets/sec, 8 slots give about 100 ms of latency
 * buffer, plenty for typical USB scheduling jitter without burning RAM.
 */
#ifndef USB_STREAM_RING_SLOTS
#define USB_STREAM_RING_SLOTS 8U
#endif

typedef struct {
    uint8_t  data[ENTROPY_PACKET_MAX_BYTES];
    uint16_t length;
} usb_slot_t;

static volatile usb_slot_t s_ring[USB_STREAM_RING_SLOTS];
static volatile uint32_t s_head = 0; /* write index, producer */
static volatile uint32_t s_tail = 0; /* read index, consumer */
static volatile bool     s_tx_in_flight = false;
static volatile usb_stream_stats_t s_stats = {0};

static inline uint32_t ring_count(void)
{
    return s_head - s_tail; /* uint32 difference handles wrap */
}

void usb_stream_init(void)
{
    s_head = 0;
    s_tail = 0;
    s_tx_in_flight = false;
    memset((void *)&s_stats, 0, sizeof s_stats);
}

bool usb_stream_send(const uint8_t *data, size_t len)
{
    if (data == NULL || len == 0 || len > ENTROPY_PACKET_MAX_BYTES) {
        return false;
    }
    if (ring_count() >= USB_STREAM_RING_SLOTS) {
        s_stats.dropped++;
        return false;
    }
    const uint32_t slot_idx = s_head % USB_STREAM_RING_SLOTS;
    usb_slot_t *slot = (usb_slot_t *)&s_ring[slot_idx];
    memcpy(slot->data, data, len);
    slot->length = (uint16_t)len;

    /* Publish: bump head after writing the slot. */
    s_head = s_head + 1;
    s_stats.enqueued++;
    s_stats.queue_depth = ring_count();

    /* Kick off transmission immediately if idle. The pump is safe to call
     * from any context.
     */
    usb_stream_pump();
    return true;
}

void usb_stream_pump(void)
{
    if (s_tx_in_flight) {
        return;
    }
    if (ring_count() == 0) {
        return;
    }
    const uint32_t slot_idx = s_tail % USB_STREAM_RING_SLOTS;
    usb_slot_t *slot = (usb_slot_t *)&s_ring[slot_idx];

    s_tx_in_flight = true;
    uint8_t rc = CDC_Transmit_FS(slot->data, slot->length);
    if (rc == USBD_OK) {
        /* Will be released by the completion callback. */
        return;
    }
    /* Transmit failed; back off without releasing the slot. The main loop
     * pump will retry on the next tick.
     */
    s_tx_in_flight = false;
    s_stats.tx_failed++;
}

usb_stream_stats_t usb_stream_get_stats(void)
{
    usb_stream_stats_t snap;
    snap.enqueued    = s_stats.enqueued;
    snap.transmitted = s_stats.transmitted;
    snap.dropped     = s_stats.dropped;
    snap.tx_failed   = s_stats.tx_failed;
    snap.queue_depth = ring_count();
    return snap;
}

/* Called by the user-provided CDC_TransmitCplt_FS in usbd_cdc_if.c.
 * The CubeMX-generated weak definition does nothing; replace its body with
 * a single call to usb_stream_on_tx_complete().
 */
void usb_stream_on_tx_complete(void)
{
    s_tx_in_flight = false;
    s_tail = s_tail + 1; /* release the slot we just transmitted */
    s_stats.transmitted++;
    s_stats.queue_depth = ring_count();
    /* Submit the next packet, if any. */
    usb_stream_pump();
}

#else /* !ENTROPY_TRANSPORT_USB */

/* Stubs so the linker is happy if the module is included but USB is not
 * selected. These should not be called in practice; the build system
 * should not link this module when ENTROPY_TRANSPORT_USB is undefined.
 */

void usb_stream_init(void) {}
bool usb_stream_send(const uint8_t *data, size_t len) { (void)data; (void)len; return false; }
void usb_stream_pump(void) {}
usb_stream_stats_t usb_stream_get_stats(void) { usb_stream_stats_t s = {0}; return s; }

#endif /* ENTROPY_TRANSPORT_USB */
