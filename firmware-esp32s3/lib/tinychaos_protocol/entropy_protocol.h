/*
 * entropy_protocol.h - on-wire packet format for tinychaos.
 *
 * Portable C99. No HAL dependencies. This module is the firmware-side
 * authority for the wire protocol and matches tinychaos.protocol on the
 * Python host side, byte-for-byte. Both implementations are pinned to the
 * same CRC-16/CCITT-FALSE known-answer (0x29B1 on "123456789").
 *
 * Layout (little-endian throughout):
 *
 *   Offset  Field      Size       Notes
 *   ------  ---------  ---------  ----------------------------------------
 *   0       MAGIC      2 bytes    Literal 0xDA 0x7A
 *   2       VERSION    1 byte     Protocol version
 *   3       FLAGS      1 byte     Reserved
 *   4       SEQ        4 bytes    uint32 LE
 *   8       TIME_US    4 bytes    uint32 LE microsecond timestamp
 *   12      COUNT      2 bytes    uint16 LE number of samples
 *   14      SAMPLES    2*COUNT    uint16 LE ADC samples
 *   14+2N   CRC16      2 bytes    uint16 LE CRC over VERSION..last sample
 */

#ifndef TINYCHAOS_ENTROPY_PROTOCOL_H
#define TINYCHAOS_ENTROPY_PROTOCOL_H

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#include "entropy_config.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Parsed header, used by entropy_packet_decode_header. */
typedef struct {
    uint8_t  version;
    uint8_t  flags;
    uint32_t seq;
    uint32_t time_us;
    uint16_t count;
} entropy_header_t;

/* Compute CRC-16/CCITT-FALSE over data[0..len-1].
 * Polynomial 0x1021, initial value 0xFFFF, no input reflection, no output
 * reflection, no final XOR. KAT: crc16_ccitt_false("123456789") == 0x29B1.
 */
uint16_t crc16_ccitt_false(const uint8_t *data, size_t len);

/* Encode a packet into ``out`` and return its length in bytes, or 0 if
 * ``out_cap`` is too small or any argument is invalid.
 *
 * The encoded layout is exactly the format documented at the top of this
 * file. ``samples`` is read as ``sample_count`` little-endian uint16
 * values; on a little-endian target (Cortex-M is little-endian) this is
 * just a memcpy of 2*sample_count bytes.
 */
size_t entropy_packet_encode(uint8_t *out, size_t out_cap,
                             uint32_t seq, uint32_t time_us,
                             const uint16_t *samples, size_t sample_count);

/* Parse a header from ``in`` (assuming ``in[0]==0xDA`` and ``in[1]==0x7A``).
 * Returns true on success. On failure (buffer too short or version not 1)
 * returns false and leaves ``hdr`` untouched.
 *
 * The host implements its own decoder; this is for on-device self-tests
 * and for any future on-device introspection.
 */
bool entropy_packet_decode_header(const uint8_t *in, size_t in_len,
                                  entropy_header_t *hdr);

/* Run an internal self-test. Returns true on success.
 *
 * The self-test verifies the CRC known-answer and an encode-decode
 * round-trip against a fixed input. Cheap; safe to run at boot if you
 * want.
 */
bool entropy_protocol_self_test(void);

#ifdef __cplusplus
}
#endif

#endif /* TINYCHAOS_ENTROPY_PROTOCOL_H */
