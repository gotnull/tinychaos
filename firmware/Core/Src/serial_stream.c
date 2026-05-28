/*
 * serial_stream.c - UART transmit fallback implementation
 *
 * Mirrors usb_stream.c but uses HAL_UART_Transmit_DMA on USART3. CubeMX
 * generates the huart3 handle and the DMA stream setup; this module only
 * concerns itself with the producer-side ring buffer and with kicking off
 * the next DMA transfer when the previous one completes.
 */

#include <string.h>

#include "serial_stream.h"

#ifdef ENTROPY_TRANSPORT_UART

#include "main.h"          /* extern UART_HandleTypeDef huart3, generated */
extern UART_HandleTypeDef huart3;

#ifndef SERIAL_STREAM_RING_SLOTS
#define SERIAL_STREAM_RING_SLOTS 8U
#endif

typedef struct {
    uint8_t  data[ENTROPY_PACKET_MAX_BYTES];
    uint16_t length;
} uart_slot_t;

static volatile uart_slot_t s_ring[SERIAL_STREAM_RING_SLOTS];
static volatile uint32_t s_head = 0;
static volatile uint32_t s_tail = 0;
static volatile bool     s_tx_in_flight = false;
static volatile serial_stream_stats_t s_stats = {0};

static inline uint32_t ring_count(void) { return s_head - s_tail; }

void serial_stream_init(void)
{
    s_head = 0;
    s_tail = 0;
    s_tx_in_flight = false;
    memset((void *)&s_stats, 0, sizeof s_stats);
}

bool serial_stream_send(const uint8_t *data, size_t len)
{
    if (data == NULL || len == 0 || len > ENTROPY_PACKET_MAX_BYTES) {
        return false;
    }
    if (ring_count() >= SERIAL_STREAM_RING_SLOTS) {
        s_stats.dropped++;
        return false;
    }
    const uint32_t slot_idx = s_head % SERIAL_STREAM_RING_SLOTS;
    uart_slot_t *slot = (uart_slot_t *)&s_ring[slot_idx];
    memcpy(slot->data, data, len);
    slot->length = (uint16_t)len;
    s_head++;
    s_stats.enqueued++;
    s_stats.queue_depth = ring_count();
    serial_stream_pump();
    return true;
}

void serial_stream_pump(void)
{
    if (s_tx_in_flight) {
        return;
    }
    if (ring_count() == 0) {
        return;
    }
    const uint32_t slot_idx = s_tail % SERIAL_STREAM_RING_SLOTS;
    uart_slot_t *slot = (uart_slot_t *)&s_ring[slot_idx];

    s_tx_in_flight = true;
    HAL_StatusTypeDef rc = HAL_UART_Transmit_DMA(&huart3, slot->data, slot->length);
    if (rc == HAL_OK) {
        return;
    }
    s_tx_in_flight = false;
    s_stats.tx_failed++;
}

serial_stream_stats_t serial_stream_get_stats(void)
{
    serial_stream_stats_t snap;
    snap.enqueued    = s_stats.enqueued;
    snap.transmitted = s_stats.transmitted;
    snap.dropped     = s_stats.dropped;
    snap.tx_failed   = s_stats.tx_failed;
    snap.queue_depth = ring_count();
    return snap;
}

void serial_stream_on_tx_complete(void)
{
    s_tx_in_flight = false;
    s_tail++;
    s_stats.transmitted++;
    s_stats.queue_depth = ring_count();
    serial_stream_pump();
}

#else /* !ENTROPY_TRANSPORT_UART */

void serial_stream_init(void) {}
bool serial_stream_send(const uint8_t *d, size_t l) { (void)d; (void)l; return false; }
void serial_stream_pump(void) {}
serial_stream_stats_t serial_stream_get_stats(void) { serial_stream_stats_t s = {0}; return s; }
void serial_stream_on_tx_complete(void) {}

#endif /* ENTROPY_TRANSPORT_UART */
