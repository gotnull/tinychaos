/*
 * test_protocol_host.c - host-side self test for entropy_protocol.c
 *
 * Compiled with gcc or clang (NOT arm-none-eabi-gcc). Verifies that the
 * firmware-side protocol module produces byte-identical output to the
 * Python reference for a fixed input vector, and that the CRC known-answer
 * matches the canonical value.
 *
 * Build:
 *   make -C firmware test
 *
 * Run:
 *   ./firmware/build-test/test_protocol_host
 *
 * The companion script tools/scripts/check_protocol_parity.py (if added)
 * encodes the same fixed input via the Python reference and compares the
 * two byte streams. For now we just print the encoded bytes so a human (or
 * a follow-up script) can diff them.
 */

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "entropy_protocol.h"

static int fail_count = 0;

#define CHECK(cond, msg) do { \
    if (!(cond)) { \
        fprintf(stderr, "FAIL: %s (line %d)\n", msg, __LINE__); \
        fail_count++; \
    } \
} while (0)

static void hexdump(const char *label, const uint8_t *buf, size_t n)
{
    printf("%s [%zu bytes]:", label, n);
    for (size_t i = 0; i < n; ++i) {
        if ((i % 16) == 0) printf("\n  ");
        printf("%02X ", buf[i]);
    }
    printf("\n");
}

int main(void)
{
    /* ---- CRC known-answer ---- */
    const uint8_t kat[] = { '1','2','3','4','5','6','7','8','9' };
    uint16_t crc = crc16_ccitt_false(kat, sizeof kat);
    CHECK(crc == 0x29B1, "CRC KAT 123456789 -> 0x29B1");

    /* ---- Self-test ---- */
    CHECK(entropy_protocol_self_test(), "entropy_protocol_self_test()");

    /* ---- Fixed-input encode ---- */
    uint8_t buf[ENTROPY_HEADER_SIZE + 2 * 4 + ENTROPY_CRC_SIZE];
    const uint16_t samples[4] = { 0x0102U, 0x0304U, 0x0506U, 0x0708U };
    size_t n = entropy_packet_encode(buf, sizeof buf,
                                     0x11223344U, 0x55667788U,
                                     samples, 4);
    CHECK(n == sizeof buf, "encoded length");

    /* Compare bytes against the documented layout. */
    CHECK(buf[0] == 0xDA && buf[1] == 0x7A, "magic");
    CHECK(buf[2] == 1, "version");
    CHECK(buf[3] == 0, "flags");
    /* SEQ little-endian: 0x11223344 -> 44 33 22 11 */
    CHECK(buf[4] == 0x44 && buf[5] == 0x33 && buf[6] == 0x22 && buf[7] == 0x11,
          "seq LE");
    /* TIME_US little-endian: 0x55667788 -> 88 77 66 55 */
    CHECK(buf[8] == 0x88 && buf[9] == 0x77 && buf[10] == 0x66 && buf[11] == 0x55,
          "time_us LE");
    /* COUNT little-endian: 4 -> 04 00 */
    CHECK(buf[12] == 0x04 && buf[13] == 0x00, "count LE");
    /* SAMPLES: 0x0102 0x0304 0x0506 0x0708 -> 02 01 04 03 06 05 08 07 */
    CHECK(buf[14] == 0x02 && buf[15] == 0x01, "sample 0 LE");
    CHECK(buf[16] == 0x04 && buf[17] == 0x03, "sample 1 LE");
    CHECK(buf[18] == 0x06 && buf[19] == 0x05, "sample 2 LE");
    CHECK(buf[20] == 0x08 && buf[21] == 0x07, "sample 3 LE");

    /* CRC at offset 22, 2 bytes. */
    uint16_t crc_in_packet = (uint16_t)buf[22] | ((uint16_t)buf[23] << 8);
    uint16_t crc_computed = crc16_ccitt_false(&buf[2], 20);
    CHECK(crc_in_packet == crc_computed, "CRC matches computed value");

    hexdump("encoded packet", buf, n);

    if (fail_count) {
        fprintf(stderr, "%d check(s) FAILED\n", fail_count);
        return 1;
    }
    printf("all checks passed\n");
    return 0;
}
