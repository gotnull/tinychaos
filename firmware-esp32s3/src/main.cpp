// tinychaos ESP32-S3 firmware.
//
// Reads one analog input continuously (ADC1_CH0 = GPIO 1 by default), wraps
// the samples in the tinychaos wire-protocol frame, and streams the frames
// to the host over USB CDC. WiFi + ArduinoOTA + on-device GitHub-releases
// OTA on top so you can push new firmware over the air once the device is
// on your network.
//
// The on-device UI uses the same renderer (PSRAM virtual frame, native-
// stripe DMA flush, 5x7 TinyGlyph font) as rsvpnano so menus are pixel-
// faithful to the rsvpnano look. Single-button nav on the BOOT button:
//   short press  : move selection down (wraps)
//   long  press  : activate the selected item
//
// The wire format is byte-for-byte identical to the STM32 firmware in
// ../firmware/, the Python host (tools/), and the C# host (analysis/).
// See ../docs/ENTROPY_CAPTURE_PIPELINE.md section 8 for the spec.

#include <Arduino.h>
#include <Wire.h>
#include <WiFi.h>

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

static volatile bool adc_batch_ready = false;

static Display       gDisplay;
static OtaUpdater    gOta;
static ButtonHandler gBootButton(BoardConfig::PIN_BOOT_BUTTON);
static TouchHandler  gTouch;
static OtaUi         gUi(gDisplay, gOta, gBootButton, gTouch);

static uint32_t gLastWifiPollMs = 0;
static bool     gWifiReportedConnected = false;

static void IRAM_ATTR onAdcBatchReady()
{
  // ISR. Keep tiny. The main loop polls adc_batch_ready and calls
  // analogContinuousRead() to actually pick up the data.
  adc_batch_ready = true;
}

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
    Serial0.println("[wifi] SSID empty in wifi_config.h, skipping");
    return;
  }
  WiFi.mode(WIFI_STA);
  WiFi.setHostname(TINYCHAOS_HOSTNAME);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  Serial0.printf("[wifi] connecting to %s\n", WIFI_SSID);
}

static void pollWifiForUi(uint32_t nowMs)
{
  if (nowMs - gLastWifiPollMs < 250) return;
  gLastWifiPollMs = nowMs;
  const bool connected = (WiFi.status() == WL_CONNECTED);
  if (connected != gWifiReportedConnected) {
    gWifiReportedConnected = connected;
    if (connected) {
      Serial0.printf("[wifi] connected, ip=%s\n", WiFi.localIP().toString().c_str());
    } else {
      Serial0.println("[wifi] disconnected");
    }
  }
  gUi.setWifiState(connected, String(WIFI_SSID),
                   connected ? WiFi.localIP().toString() : String(""));
}

// ---- Main loop ------------------------------------------------------------

void setup()
{
  // Explicit RX/TX pins (Waveshare ESP32-S3 R8 OPI wires CH343 to GPIO 44/43,
  // which are also the ESP32-S3's default UART0 pins — being explicit makes
  // this independent of framework defaults).
  Serial0.begin(921600, SERIAL_8N1, /*rx*/44, /*tx*/43);
  delay(200);
  Serial0.printf("\n\n[boot] tinychaos esp32-s3, build=%s\n", TINYCHAOS_BUILD_TAG);
  Serial0.flush();

  gBootButton.begin();

  if (!gDisplay.begin()) {
    Serial0.println("[display] begin failed (no PSRAM?); UI disabled");
  } else {
    Serial0.println("[display] ready");
  }

  // Touch lives on its own I2C bus (PIN_TOUCH_SDA/SCL). The Display.cpp
  // transpose uses the "uiRotated_=false" mapping, so the touch handler
  // must do the same axis convention (mappedX/mappedY direct) to land
  // touches on the correct row.
  Wire.begin(BoardConfig::PIN_TOUCH_SDA, BoardConfig::PIN_TOUCH_SCL);
  gTouch.setUiRotated(false);
  gTouch.begin();

  gUi.begin();

  startWifi();

  setupAdc();
  Serial0.printf("[adc] continuous on GPIO%u @ %u Hz, %u samples/batch\n",
                ADC_PIN, SAMPLES_PER_S, (unsigned)SAMPLES_PER_BATCH);
  Serial0.println("[boot] streaming packets to UART0 (CH343)");
  Serial0.flush();
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
    Serial0.write(packet_out, n);
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

  // Heartbeat: prove loop() runs even before packets start flowing. Removes
  // itself from the timeline once the magic-byte stream is up.
  static uint32_t lastHeartbeatMs = 0;
  if (now - lastHeartbeatMs >= 1000) {
    lastHeartbeatMs = now;
    Serial0.write('.');
  }
}
