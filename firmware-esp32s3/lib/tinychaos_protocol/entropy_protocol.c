/*
 * entropy_protocol.c - on-wire packet implementation.
 *
 * Portable C99. No HAL dependencies. Compiles equally with arm-none-eabi-gcc
 * targeting the STM32H7 and with host gcc/clang for self-tests.
 */

#include <string.h>

#include "entropy_protocol.h"

/* ---- CRC-16/CCITT-FALSE ----
 * Bytewise implementation, no table. The hot path is dominated by USB
 * transmission, not by CRC computation, so the trade-off favours small
 * code size over a 512-byte lookup table. If profiling proves otherwise,
 * the table variant is a drop-in replacement.
 */

#define CRC16_POLY  ((uint16_t)0x1021)
#define CRC16_INIT  ((uint16_t)0xFFFF)

uint16_t crc16_ccitt_false(const uint8_t *data, size_t len)
{
    uint16_t crc = CRC16_INIT;
    for (size_t i = 0; i < len; ++i) {
        crc ^= (uint16_t)data[i] << 8;
        for (int b = 0; b < 8; ++b) {
            if (crc & 0x8000U) {
                crc = (uint16_t)((crc << 1) ^ CRC16_POLY);
            } else {
                crc = (uint16_t)(crc << 1);
            }
        }
    }
    return crc;
}

/* ---- Little-endian writers ----
 * Cortex-M is natively little-endian. We still use explicit byte ops here
 * so the code is portable to any other target (and to host self-test
 * builds), and so the wire format is invariant to compiler choices.
 */

static inline void le_write_u16(uint8_t *p, uint16_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)(v >> 8);
}

static inline void le_write_u32(uint8_t *p, uint32_t v)
{
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

static inline uint16_t le_read_u16(const uint8_t *p)
{
    return (uint16_t)((uint16_t)p[0] | ((uint16_t)p[1] << 8));
}

static inline uint32_t le_read_u32(const uint8_t *p)
{
    return ((uint32_t)p[0])
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

/* ---- Encode ---- */

size_t entropy_packet_encode(uint8_t *out, size_t out_cap,
                             uint32_t seq, uint32_t time_us,
                             const uint16_t *samples, size_t sample_count)
{
    if (out == NULL) {
        return 0;
    }
    if (sample_count > 0xFFFFU) {
        return 0;
    }
    if (sample_count > 0 && samples == NULL) {
        return 0;
    }

    const size_t total = ENTROPY_HEADER_SIZE + 2U * sample_count + ENTROPY_CRC_SIZE;
    if (total > out_cap) {
        return 0;
    }

    /* Header (16 bytes incl. magic, of which 14 bytes are the CRC scope). */
    out[0] = ENTROPY_MAGIC_0;
    out[1] = ENTROPY_MAGIC_1;
    out[2] = ENTROPY_PROTOCOL_VERSION;
    out[3] = 0; /* flags */
    le_write_u32(&out[4], seq);
    le_write_u32(&out[8], time_us);
    le_write_u16(&out[12], (uint16_t)sample_count);

    /* Samples block: write each uint16 LE explicitly so the wire format does
     * not depend on the host endianness or on unaligned-access semantics.
     */
    uint8_t *body = &out[ENTROPY_HEADER_SIZE];
    for (size_t i = 0; i < sample_count; ++i) {
        le_write_u16(&body[2U * i], samples[i]);
    }

    /* CRC over VERSION (offset 2) through last sample. */
    const size_t crc_scope_len = (ENTROPY_HEADER_SIZE - 2U) + 2U * sample_count;
    const uint16_t crc = crc16_ccitt_false(&out[2], crc_scope_len);
    le_write_u16(&out[ENTROPY_HEADER_SIZE + 2U * sample_count], crc);

    return total;
}

/* ---- Decode header ---- */

bool entropy_packet_decode_header(const uint8_t *in, size_t in_len,
                                  entropy_header_t *hdr)
{
    if (in == NULL || hdr == NULL) {
        return false;
    }
    if (in_len < ENTROPY_HEADER_SIZE) {
        return false;
    }
    if (in[0] != ENTROPY_MAGIC_0 || in[1] != ENTROPY_MAGIC_1) {
        return false;
    }
    const uint8_t version = in[2];
    if (version != ENTROPY_PROTOCOL_VERSION) {
        return false;
    }
    hdr->version = version;
    hdr->flags = in[3];
    hdr->seq = le_read_u32(&in[4]);
    hdr->time_us = le_read_u32(&in[8]);
    hdr->count = le_read_u16(&in[12]);
    return true;
}

/* ---- Self-test ---- */

bool entropy_protocol_self_test(void)
{
    /* CRC known-answer: "123456789" -> 0x29B1. */
    const uint8_t kat_in[] = { '1','2','3','4','5','6','7','8','9' };
    if (crc16_ccitt_false(kat_in, sizeof kat_in) != 0x29B1U) {
        return false;
    }

    /* Encode and decode-header round-trip with a fixed input. */
    uint8_t buf[ENTROPY_HEADER_SIZE + 2 * 4 + ENTROPY_CRC_SIZE];
    const uint16_t samples[4] = { 0x0001U, 0x0002U, 0x0003U, 0x0004U };
    size_t n = entropy_packet_encode(buf, sizeof buf,
                                     0xDEADBEEFU, 0x01234567U,
                                     samples, 4);
    if (n != sizeof buf) {
        return false;
    }

    entropy_header_t hdr;
    if (!entropy_packet_decode_header(buf, n, &hdr)) {
        return false;
    }
    if (hdr.version != ENTROPY_PROTOCOL_VERSION) return false;
    if (hdr.flags != 0) return false;
    if (hdr.seq != 0xDEADBEEFU) return false;
    if (hdr.time_us != 0x01234567U) return false;
    if (hdr.count != 4) return false;

    /* Verify the CRC bytes match what we recompute. */
    const size_t crc_scope_len = (ENTROPY_HEADER_SIZE - 2U) + 2U * 4U;
    const uint16_t crc_computed = crc16_ccitt_false(&buf[2], crc_scope_len);
    const uint16_t crc_in_packet = le_read_u16(&buf[ENTROPY_HEADER_SIZE + 2U * 4U]);
    if (crc_computed != crc_in_packet) {
        return false;
    }

    return true;
}
