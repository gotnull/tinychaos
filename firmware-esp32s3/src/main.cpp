// tinychaos ESP32-S3 firmware.
//
// Reads one analog input continuously (ADC1_CH0 = GPIO 1 by default), wraps
// the samples in the tinychaos wire-protocol frame, and streams the frames
// to the host through UART0 / CH343. WiFi + on-device GitHub-releases OTA
// on top so you can push new firmware over the air.
//
// On-device UI uses the same renderer (PSRAM virtual frame, native-stripe
// DMA flush, 5x7 TinyGlyph font) as rsvpnano so menus are pixel-faithful
// to the rsvpnano look. Touch nav: tap a menu row to activate, swipe
// vertically to scroll. Any tap also raises FLAGS bit 0 on the next
// outgoing packet so the host-side GUI can switch tabs to its live view.
//
// SERIAL TRANSPORT (Waveshare ESP32-S3 R8 OPI specific):
// UART0 / CH343 is the only USB endpoint on this board, and it doubles as
// the ESP-IDF console. We write packets via stdout (fwrite) since that is
// the path the framework already has working on UART0. The one thing that
// corrupts the binary stream is the WiFi driver's ROM-level ets_printf
// during a scan/connect (it bypasses esp_log silencing). So WiFi is OFF by
// default and only powers up when the user picks "Check for update" on the
// device menu. With WiFi quiet, the packet stream stays clean.
//
// The wire format is byte-for-byte identical to the STM32 firmware in
// ../firmware/, the Python host (tools/), and the C# host (analysis/).
// See ../docs/ENTROPY_CAPTURE_PIPELINE.md section 8 for the spec.

#include <Arduino.h>
#include <Wire.h>
#include <WiFi.h>
#include <esp_log.h>
#include <esp_adc/adc_continuous.h>

// The Arduino core links its logging with -Wl,--wrap=log_printf and expects a
// __wrap_log_printf definition. The from-source hybrid build that
// custom_sdkconfig triggers leaves that symbol undefined (chip-debug-report.cpp
// references it), failing the link. Provide a no-op: it satisfies the linker
// and harmlessly discards any framework log text.
extern "C" int __wrap_log_printf(const char * /*fmt*/, ...) { return 0; }

extern "C"
{
#include "entropy_config.h"
#include "entropy_protocol.h"
}

#include "OtaUi.h"
#include "OtaUpdater.h"
#include "board/BoardConfig.h"
#include "display/Display.h"
#include "input/ButtonHandler.h"
#include "input/TouchHandler.h"
#include "wifi_config.h"

// ---- Capture parameters ---------------------------------------------------

static constexpr uint8_t  ADC_PIN          = 1;      // GPIO1 / ADC1_CH0
static constexpr uint32_t SAMPLES_PER_S    = 20000;  // 20 kHz oneshot rate
static constexpr size_t   SAMPLES_PER_BATCH = PACKET_SAMPLE_COUNT;

// ---- Runtime state --------------------------------------------------------

static uint8_t  packet_out[ENTROPY_PACKET_MAX_BYTES];
static uint16_t sample_buf[SAMPLES_PER_BATCH];
static uint32_t seq = 0;

// Wire-protocol FLAGS bit 0 = "user tapped the device screen since the
// previous packet". The host-side GUI watches this bit on every header to
// auto-switch to its live-waveform view. Bit reuses the protocol's
// previously-reserved FLAGS byte; older parsers ignore it.
static constexpr uint8_t ENTROPY_FLAG_USER_TAP = 0x01;

// IDF continuous-ADC handle + raw conversion frame. We read true per-sample
// 12-bit conversions (NOT the averaged one-value-per-pin that the Arduino
// analogContinuous wrapper produces — averaging would destroy the avalanche
// noise we are trying to capture). One conversion = SOC_ADC_DIGI_RESULT_BYTES
// (4 on the S3); we accumulate PACKET_SAMPLE_COUNT raw samples per packet.
static adc_continuous_handle_t gAdc = nullptr;
static uint8_t  adc_frame[PACKET_SAMPLE_COUNT * SOC_ADC_DIGI_RESULT_BYTES];
static size_t   wave_fill = 0;   // raw samples accumulated into sample_buf

static Display       gDisplay;
static OtaUpdater    gOta;
static ButtonHandler gBootButton(BoardConfig::PIN_BOOT_BUTTON);
static TouchHandler  gTouch;
static OtaUi         gUi(gDisplay, gOta, gBootButton, gTouch);

static bool     gWifiStarted = false;

// Packet/text writer: stdout shares the framework's working UART0 console.
// LF->CRLF translation is disabled at compile time (CONFIG_NEWLIB_STDOUT_
// LINE_ENDING_LF in platformio.ini), so fwrite emits packet bytes verbatim.
// _IONBF (set in setup) keeps 0x0A bytes from triggering mid-packet flushes.
static inline void uart0_write(const uint8_t *data, size_t n) {
  fwrite(data, 1, n, stdout);
  fflush(stdout);
}

static inline void uart0_print(const char *s) {
  fputs(s, stdout);
  fflush(stdout);
}

// ---- Setup helpers --------------------------------------------------------

static void setupAdc()
{
  adc_continuous_handle_cfg_t handleCfg = {};
  handleCfg.max_store_buf_size = sizeof(adc_frame) * 4;   // a few frames of slack
  handleCfg.conv_frame_size    = sizeof(adc_frame);       // one packet's worth
  if (adc_continuous_new_handle(&handleCfg, &gAdc) != ESP_OK) {
    uart0_print("[adc] new_handle failed\n");
    return;
  }

  adc_digi_pattern_config_t pattern = {};
  pattern.atten     = ADC_ATTEN_DB_12;     // full ~0..3.3 V range
  pattern.channel   = ADC_CHANNEL_0;       // GPIO1 = ADC1_CH0 on the S3
  pattern.unit      = ADC_UNIT_1;
  pattern.bit_width = ADC_BITWIDTH_12;

  adc_continuous_config_t digCfg = {};
  digCfg.sample_freq_hz = SAMPLES_PER_S;
  digCfg.conv_mode      = ADC_CONV_SINGLE_UNIT_1;
  digCfg.format         = ADC_DIGI_OUTPUT_FORMAT_TYPE2;   // S3 frame layout
  digCfg.pattern_num    = 1;
  digCfg.adc_pattern    = &pattern;
  if (adc_continuous_config(gAdc, &digCfg) != ESP_OK) {
    uart0_print("[adc] config failed\n");
    return;
  }
  adc_continuous_start(gAdc);
}

// Bring WiFi up on demand and block until connected or timeout. Called from
// the OtaUi "Check for update" / "Apply" path only — never at boot — so the
// scan-time ets_printf noise never overlaps the packet stream during normal
// streaming. Returns true once associated with an IP.
static bool ensureWifiConnected()
{
  if (sizeof(WIFI_SSID) <= 1) return false;          // no creds compiled in
  if (WiFi.status() == WL_CONNECTED) return true;

  if (!gWifiStarted) {
    WiFi.mode(WIFI_STA);
    WiFi.setHostname(TINYCHAOS_HOSTNAME);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    gWifiStarted = true;
  }
  const uint32_t deadline = millis() + 15000;
  while (WiFi.status() != WL_CONNECTED && millis() < deadline) {
    delay(100);
  }
  return WiFi.status() == WL_CONNECTED;
}

// ---- Main loop ------------------------------------------------------------

void setup()
{
  // stdout shares the framework's UART0 console; LF->CRLF translation is off
  // at compile time so packet bytes go out verbatim. _IONBF disables line
  // buffering so 0x0A bytes don't flush mid-packet. Silence framework
  // ESP_LOG (the other UART0 noise source besides WiFi, which stays off).
  setvbuf(stdout, NULL, _IONBF, 0);
  esp_log_level_set("*", ESP_LOG_NONE);
  delay(200);

  char boot_line[96];
  int n = snprintf(boot_line, sizeof(boot_line),
                   "\n\n[boot] tinychaos esp32-s3, build=%s\n",
                   TINYCHAOS_BUILD_TAG);
  uart0_write(reinterpret_cast<const uint8_t *>(boot_line), n);

  gBootButton.begin();

  if (!gDisplay.begin()) {
    uart0_print("[display] begin failed (no PSRAM?); UI disabled\n");
  } else {
    uart0_print("[display] ready\n");
  }

  Wire.begin(BoardConfig::PIN_TOUCH_SDA, BoardConfig::PIN_TOUCH_SCL);
  gTouch.setUiRotated(false);
  gTouch.begin();

  // Hand the UI a way to bring WiFi up only when the user asks for an
  // update; WiFi stays OFF during normal streaming so its scan noise never
  // corrupts the packet stream.
  gUi.setWifiConnectFn(&ensureWifiConnected);
  gUi.begin();

  setupAdc();
  char adc_line[160];
  int an = snprintf(adc_line, sizeof(adc_line),
                    "[adc] continuous on GPIO%u @ %u Hz, %u samples/batch\n"
                    "[boot] streaming packets to UART0 (CH343)\n",
                    ADC_PIN, SAMPLES_PER_S, (unsigned)SAMPLES_PER_BATCH);
  uart0_write(reinterpret_cast<const uint8_t *>(adc_line), an);
}

static void emitPacket()
{
  const uint32_t time_us = (uint32_t)micros();
  const size_t n = entropy_packet_encode(packet_out, sizeof(packet_out),
                                         seq++, time_us,
                                         sample_buf, SAMPLES_PER_BATCH);
  if (n == 0) return;
  // Latch a pending screen-tap into FLAGS bit 0 and refresh the CRC over the
  // header (excl. magic) + samples so the host-side GUI can react to taps.
  if (gUi.consumeTapEvent()) {
    packet_out[3] |= ENTROPY_FLAG_USER_TAP;
    const uint16_t crc = crc16_ccitt_false(&packet_out[2], n - 4);
    packet_out[n - 2] = (uint8_t)(crc & 0xFF);
    packet_out[n - 1] = (uint8_t)(crc >> 8);
  }
  uart0_write(packet_out, n);
}

static void pumpAdc()
{
  if (gAdc == nullptr) return;

  // Drain the conversion ring FULLY each call (loop until no data is ready),
  // so throughput is decoupled from the main-loop rate — a slow loop tick
  // (e.g. a display repaint) would otherwise leave the ring to overflow and
  // we'd lose ~3/4 of the 20 kHz stream. Each conversion is
  // SOC_ADC_DIGI_RESULT_BYTES wide; we accumulate raw 12-bit values and emit
  // a packet every PACKET_SAMPLE_COUNT samples.
  for (;;) {
    uint32_t out_len = 0;
    const esp_err_t r = adc_continuous_read(gAdc, adc_frame, sizeof(adc_frame),
                                            &out_len, 0);
    if (r != ESP_OK || out_len == 0) break;

    const size_t convs = out_len / SOC_ADC_DIGI_RESULT_BYTES;
    for (size_t i = 0; i < convs; i++) {
      const adc_digi_output_data_t *p =
          reinterpret_cast<const adc_digi_output_data_t *>(
              &adc_frame[i * SOC_ADC_DIGI_RESULT_BYTES]);
      // TYPE2 frame (ESP32-S3): 12-bit data + 4-bit channel. Skip the rare
      // out-of-pattern conversion the controller can emit on the first frame.
      if (p->type2.channel != ADC_CHANNEL_0) continue;
      sample_buf[wave_fill++] = (uint16_t)p->type2.data;
      if (wave_fill >= SAMPLES_PER_BATCH) {
        emitPacket();
        wave_fill = 0;
      }
    }
  }
}

void loop()
{
  pumpAdc();
  const uint32_t now = millis();
  // Report current WiFi status to the UI (connected/offline) without
  // initiating a connection — ensureWifiConnected() owns that.
  const bool connected = (WiFi.status() == WL_CONNECTED);
  gUi.setWifiState(connected, String(WIFI_SSID),
                   connected ? WiFi.localIP().toString() : String(""));
  gUi.tick(now);
}
