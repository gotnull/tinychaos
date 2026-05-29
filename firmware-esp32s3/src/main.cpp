// tinychaos ESP32-S3 firmware.
//
// Reads one analog input continuously (ADC1_CH0 = GPIO 1 by default), wraps
// the samples in the tinychaos wire-protocol frame, and streams the frames
// to the host through UART0 / CH343. WiFi + ArduinoOTA + on-device
// GitHub-releases OTA on top so you can push new firmware over the air.
//
// On-device UI uses the same renderer (PSRAM virtual frame, native-stripe
// DMA flush, 5x7 TinyGlyph font) as rsvpnano so menus are pixel-faithful
// to the rsvpnano look. Touch nav: tap a menu row to activate, swipe
// vertically to scroll. Any tap also raises FLAGS bit 0 on the next
// outgoing packet so the host-side GUI can switch tabs to its live view.
//
// SERIAL TRANSPORT CAVEAT (Waveshare ESP32-S3 R8 OPI specific):
// UART0 / CH343 is the only USB endpoint on this board, AND the framework's
// console driver claims UART0 at boot using raw-IO writes. Installing the
// proper uart_driver_install path conflicts with that and kills all output.
// We compromise: write through stdout (fwrite, the same path the framework
// uses) and silence framework logs (esp_log_level_set + WiFi is the only
// remaining noise source). Some packets still get corrupted when concurrent
// stdout writes interleave; the host-side Framer recovers via MAGIC
// resync. ~17/78 pkts/sec land cleanly; the rest are dropped/resynced.
// A clean fix needs CONFIG_ESP_CONSOLE_NONE in sdkconfig or a USB pigtail
// to the ESP32-S3 native USB pins.
//
// The wire format is byte-for-byte identical to the STM32 firmware in
// ../firmware/, the Python host (tools/), and the C# host (analysis/).
// See ../docs/ENTROPY_CAPTURE_PIPELINE.md section 8 for the spec.

#include <Arduino.h>
#include <Wire.h>
#include <WiFi.h>
#include <esp_log.h>

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

static volatile bool adc_batch_ready = false;

static Display       gDisplay;
static OtaUpdater    gOta;
static ButtonHandler gBootButton(BoardConfig::PIN_BOOT_BUTTON);
static TouchHandler  gTouch;
static OtaUi         gUi(gDisplay, gOta, gBootButton, gTouch);

static uint32_t gLastWifiPollMs        = 0;
static bool     gWifiReportedConnected = false;

static inline void uart0_write(const uint8_t *data, size_t n) {
  fwrite(data, 1, n, stdout);
  fflush(stdout);
}

static inline void uart0_print(const char *s) {
  fputs(s, stdout);
  fflush(stdout);
}

static void IRAM_ATTR onAdcBatchReady() { adc_batch_ready = true; }

// ---- Setup helpers --------------------------------------------------------

static void setupAdc()
{
  uint8_t pins[] = {ADC_PIN};
  analogContinuousSetWidth(12);
  analogContinuousSetAtten(ADC_11db);
  analogContinuous(pins, sizeof(pins) / sizeof(pins[0]),
                   SAMPLES_PER_BATCH, SAMPLES_PER_S, &onAdcBatchReady);
  analogContinuousStart();
}

static void startWifi()
{
  if (sizeof(WIFI_SSID) <= 1) {
    uart0_print("[wifi] SSID empty in wifi_config.h, skipping\n");
    return;
  }
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(TINYCHAOS_HOSTNAME);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  uart0_print("[wifi] begin\n");
}

static void pollWifiForUi(uint32_t nowMs)
{
  if (nowMs - gLastWifiPollMs < 250) return;
  gLastWifiPollMs = nowMs;
  const bool connected = (WiFi.status() == WL_CONNECTED);
  if (connected != gWifiReportedConnected) {
    gWifiReportedConnected = connected;
  }
  gUi.setWifiState(connected, String(WIFI_SSID),
                   connected ? WiFi.localIP().toString() : String(""));
}

// ---- Main loop ------------------------------------------------------------

void setup()
{
  // Use framework's existing UART0 console (raw IO). setvbuf(_IONBF)
  // disables stdout line buffering so binary packet writes containing
  // random 0x0A bytes don't trigger mid-packet flushes. Silence framework
  // ESP_LOG so it doesn't interleave with packet bytes.
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

  gUi.begin();
  startWifi();

  setupAdc();
  char adc_line[160];
  int an = snprintf(adc_line, sizeof(adc_line),
                    "[adc] continuous on GPIO%u @ %u Hz, %u samples/batch\n"
                    "[boot] streaming packets to UART0 (CH343)\n",
                    ADC_PIN, SAMPLES_PER_S, (unsigned)SAMPLES_PER_BATCH);
  uart0_write(reinterpret_cast<const uint8_t *>(adc_line), an);
}

static void pumpAdc()
{
  if (!adc_batch_ready) return;
  adc_batch_ready = false;

  adc_continuous_result_t *result = nullptr;
  if (!analogContinuousRead(&result, 0) || result == nullptr) {
    return;
  }

  for (size_t i = 0; i < SAMPLES_PER_BATCH; i++) {
    sample_buf[i] = result[i].avg_read_raw;
  }
  const uint32_t time_us = (uint32_t)micros();
  const size_t n = entropy_packet_encode(packet_out, sizeof(packet_out),
                                         seq++, time_us,
                                         sample_buf, SAMPLES_PER_BATCH);
  if (n > 0) {
    if (gUi.consumeTapEvent()) {
      packet_out[3] |= ENTROPY_FLAG_USER_TAP;
      const uint16_t crc = crc16_ccitt_false(&packet_out[2], n - 4);
      packet_out[n - 2] = (uint8_t)(crc & 0xFF);
      packet_out[n - 1] = (uint8_t)(crc >> 8);
    }
    uart0_write(packet_out, n);
  }
  // Do NOT call analogContinuousStart() here — the v3 continuous-ADC API
  // auto-feeds batches once Started; restarting per batch wedges the driver.
}

void loop()
{
  pumpAdc();
  const uint32_t now = millis();
  pollWifiForUi(now);
  gUi.tick(now);
}
