/*
 * entropy_config.h - compile-time configuration for the tinychaos firmware.
 *
 * Single source of truth for protocol constants and capture parameters.
 * Both the protocol module (portable C) and the HAL-dependent transport
 * and ADC modules include this header.
 */

#ifndef TINYCHAOS_ENTROPY_CONFIG_H
#define TINYCHAOS_ENTROPY_CONFIG_H

#include <stdint.h>
#include <stddef.h>

/* ---- Protocol constants ----
 * See docs/ENTROPY_CAPTURE_PIPELINE.md section 8 for the canonical spec.
 */

#define ENTROPY_PROTOCOL_VERSION  ((uint8_t)1)

#define ENTROPY_MAGIC_0           ((uint8_t)0xDA)
#define ENTROPY_MAGIC_1           ((uint8_t)0x7A)

/* Bytes from start of MAGIC through end of COUNT, exclusive of CRC. */
#define ENTROPY_HEADER_SIZE       ((size_t)14)

/* Bytes consumed by the trailing CRC field. */
#define ENTROPY_CRC_SIZE          ((size_t)2)

/* Minimum valid packet size (zero samples). */
#define ENTROPY_MIN_PACKET_SIZE   (ENTROPY_HEADER_SIZE + ENTROPY_CRC_SIZE)

/* ---- Capture parameters ----
 * These default values are chosen for safe bring-up. See plan section
 * "Packet sizing" in docs/ENTROPY_CAPTURE_PIPELINE.md.
 */

/* ADC sample rate, per channel, in Hz. */
#ifndef ADC_SAMPLE_RATE_HZ
#define ADC_SAMPLE_RATE_HZ        (10000U)
#endif

/* Number of interleaved channels in each packet.
 * The default firmware build samples ADC1 IN0 (zener) and ADC1 IN3 (baseline).
 */
#ifndef ADC_CHANNEL_COUNT
#define ADC_CHANNEL_COUNT         (2U)
#endif

/* Total samples in the DMA double buffer, summed across both halves.
 * Half this number is delivered per half-complete or full-complete callback.
 */
#ifndef ADC_DMA_BUFFER_SIZE
#define ADC_DMA_BUFFER_SIZE       (1024U)
#endif

/* Samples per emitted packet. Must divide the DMA half-buffer evenly so
 * each callback produces a whole number of packets.
 */
#ifndef PACKET_SAMPLE_COUNT
#define PACKET_SAMPLE_COUNT       (256U)
#endif

/* Maximum encoded packet size in bytes.
 * Layout: header (14) + 2 * samples + crc (2).
 */
#define ENTROPY_PACKET_MAX_BYTES  (ENTROPY_HEADER_SIZE + 2U * PACKET_SAMPLE_COUNT + ENTROPY_CRC_SIZE)

/* ---- Transport selection ----
 * Define exactly one of these in the build system (or in main.c) to pick
 * the active output transport. USB CDC is preferred; UART is the fallback
 * for early bring-up before USB CDC is wired in.
 */

/* #define ENTROPY_TRANSPORT_USB */
/* #define ENTROPY_TRANSPORT_UART */

#endif /* TINYCHAOS_ENTROPY_CONFIG_H */
