// tinychaos ESP32-S3 firmware.
//
// Reads one analog input continuously (ADC1_CH0 = GPIO 1 by default), wraps
// the samples in the tinychaos wire-protocol frame, and streams the frames
// to the host over USB CDC. WiFi + ArduinoOTA + ElegantOTA on top so you
// can push new firmware over the air once the device is on your network.
//
// The wire format is byte-for-byte identical to the STM32 firmware in
// ../firmware/, the Python host (tools/), and the C# host (analysis/).
// See ../docs/ENTROPY_CAPTURE_PIPELINE.md section 8 for the spec.

#include <Arduino.h>

extern "C"
{
#include "entropy_config.h"
#include "entropy_protocol.h"
}

// ---- Capture parameters ---------------------------------------------------
//
// The Arduino-ESP32 analogContinuous API delivers one batch of
// SAMPLES_PER_BATCH readings per call to analogContinuousRead(). We size
// the batch to exactly match PACKET_SAMPLE_COUNT so one batch becomes one
// packet, no extra buffering needed.

static constexpr uint8_t ADC_PIN = 1;            // GPIO1 / ADC1_CH0
static constexpr uint32_t SAMPLES_PER_S = 20000; // 20 kHz oneshot rate
static constexpr size_t SAMPLES_PER_BATCH = PACKET_SAMPLE_COUNT;

// ---- Runtime state --------------------------------------------------------

static uint8_t packet_out[ENTROPY_PACKET_MAX_BYTES];
static uint16_t sample_buf[SAMPLES_PER_BATCH];
static uint32_t seq = 0;

static volatile bool adc_batch_ready = false;

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

  setupAdc();
  Serial0.printf("[adc] continuous on GPIO%u @ %u Hz, %u samples/batch\n",
                ADC_PIN, SAMPLES_PER_S, (unsigned)SAMPLES_PER_BATCH);
  Serial0.println("[boot] streaming packets to UART0 (CH343)");
  Serial0.flush();
}

static void pumpAdc()
{
  if (!adc_batch_ready)
    return;
  adc_batch_ready = false;

  adc_continuous_result_t *result = nullptr;
  if (!analogContinuousRead(&result, 0) || result == nullptr)
  {
    return;
  }

  // Copy the raw 12-bit readings into our flat sample buffer, then frame
  // them and emit the packet over USB CDC.
  for (size_t i = 0; i < SAMPLES_PER_BATCH; i++)
  {
    sample_buf[i] = result[i].avg_read_raw;
  }
  const uint32_t time_us = (uint32_t)micros();
  const size_t n = entropy_packet_encode(packet_out, sizeof(packet_out),
                                         seq++, time_us,
                                         sample_buf, SAMPLES_PER_BATCH);
  if (n > 0)
  {
    Serial0.write(packet_out, n);
  }

  // Note: do NOT call analogContinuousStart() here. The Arduino-ESP32 v3
  // continuous-ADC API auto-feeds batches once Started in setup(); restarting
  // every batch puts the driver into an error state and analogContinuousRead
  // stops returning data.
}

void loop()
{
  pumpAdc();

  // Heartbeat: prove loop() runs even when pumpAdc returns no data.
  // Removes itself from the timeline as soon as packets start flowing
  // (the magic-byte stream is far easier to spot than these dots).
  static uint32_t lastHeartbeatMs = 0;
  const uint32_t now = millis();
  if (now - lastHeartbeatMs >= 500) {
    lastHeartbeatMs = now;
    Serial0.write('.');
  }
}
