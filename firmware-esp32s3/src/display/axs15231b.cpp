#include "display/axs15231b.h"

#include <driver/spi_master.h>
#include <esp_log.h>

#include "board/BoardConfig.h"

namespace {

constexpr int kSpiFrequency = 40000000;
constexpr int kSendBufferPixels = 0x4000;
static const char *kAxs15231bTag = "axs15231b";

struct LcdCommand {
  uint8_t cmd;
  uint8_t data[4];
  uint8_t len;
  uint16_t delayMs;
};

constexpr LcdCommand kQspiInit[] = {
    {0x11, {0x00}, 0, 100},
    {0x36, {0x00}, 1, 0},
    {0x3A, {0x55}, 1, 0},
    {0x11, {0x00}, 0, 100},
    {0x29, {0x00}, 0, 100},
};

spi_device_handle_t gSpi = nullptr;
bool gBusReady = false;
bool gBacklightOn = false;
uint8_t gBrightnessPercent = 100;

// Pool of transaction structs reused across the (single-pipeline) queue.
// kMaxPendingTxns must be >= the most chunks a single PushColorsBegin can
// queue plus its setColumnWindow. With kSendBufferPixels = 0x4000 (16384)
// and our largest single push being a full panel (172 × 640 = 110080 px),
// that's ceil(110080 / 16384) = 7 chunks. Add one for the column window and
// round up — 12 leaves slack.
constexpr int kMaxPendingTxns = 12;
spi_transaction_ext_t gPendingTxns[kMaxPendingTxns];
int gPendingHead = 0;          // next free txn slot
int gPendingCount = 0;         // queued-but-not-yet-awaited count

void writeBacklightPwm() {
  pinMode(BoardConfig::PIN_LCD_BACKLIGHT, OUTPUT);
  // Arduino-ESP32 v3 changed these to require the pin as the first arg.
  analogWriteResolution(BoardConfig::PIN_LCD_BACKLIGHT, 8);
  analogWriteFrequency(BoardConfig::PIN_LCD_BACKLIGHT, 50000);

  if (!gBacklightOn) {
    analogWrite(BoardConfig::PIN_LCD_BACKLIGHT, 255);
    return;
  }

  // Waveshare drives the LCD backlight as active-low PWM; lower duty is brighter.
  const uint8_t brightness = gBrightnessPercent == 0 ? 1 : gBrightnessPercent;
  const uint8_t activeDuty =
      static_cast<uint8_t>((static_cast<uint16_t>(brightness) * 255U) / 100U);
  analogWrite(BoardConfig::PIN_LCD_BACKLIGHT, 255 - activeDuty);
}

void setBacklight(bool on) {
  gBacklightOn = on;
  writeBacklightPwm();
}

void sendCommand(uint8_t command, const uint8_t *data, uint32_t length) {
  if (gSpi == nullptr) {
    return;
  }

  // IMPORTANT: ESP-IDF's spi_master doesn't reliably mix polling-mode and
  // queued-mode transactions on the same device — polling can stall if the
  // queue has any leftover state. Since axs15231bPushColorsBegin uses
  // queue_trans for pixel chunks, EVERY transaction here (init commands,
  // setColumnWindow before each push) must also go through queue_trans +
  // get_trans_result. Pre-D this was polling_transmit and "worked" because
  // it was the only path; mixing modes broke the screensaver intermittently.
  spi_transaction_t transaction = {};
  transaction.flags = SPI_TRANS_MULTILINE_CMD | SPI_TRANS_MULTILINE_ADDR;
  transaction.cmd = 0x02;
  transaction.addr = static_cast<uint32_t>(command) << 8;
  if (length != 0) {
    transaction.tx_buffer = data;
    transaction.length = length * 8;
  }

  // Queue + wait synchronously so the local `transaction` struct stays
  // valid for the duration the driver needs the pointer.
  ESP_ERROR_CHECK(spi_device_queue_trans(gSpi, &transaction, portMAX_DELAY));
  spi_transaction_t *finished = nullptr;
  ESP_ERROR_CHECK(spi_device_get_trans_result(gSpi, &finished, portMAX_DELAY));
}

void setColumnWindow(uint16_t x1, uint16_t x2) {
  const uint8_t data[] = {
      static_cast<uint8_t>(x1 >> 8),
      static_cast<uint8_t>(x1),
      static_cast<uint8_t>(x2 >> 8),
      static_cast<uint8_t>(x2),
  };
  sendCommand(0x2A, data, sizeof(data));
}

}  // namespace

void axs15231bInit() {
  setBacklight(false);

  pinMode(BoardConfig::PIN_LCD_RST, OUTPUT);
  digitalWrite(BoardConfig::PIN_LCD_RST, HIGH);
  delay(30);
  digitalWrite(BoardConfig::PIN_LCD_RST, LOW);
  delay(250);
  digitalWrite(BoardConfig::PIN_LCD_RST, HIGH);
  delay(30);

  if (!gBusReady) {
    spi_bus_config_t busConfig = {};
    busConfig.data0_io_num = BoardConfig::PIN_LCD_DATA0;
    busConfig.data1_io_num = BoardConfig::PIN_LCD_DATA1;
    busConfig.sclk_io_num = BoardConfig::PIN_LCD_SCLK;
    busConfig.data2_io_num = BoardConfig::PIN_LCD_DATA2;
    busConfig.data3_io_num = BoardConfig::PIN_LCD_DATA3;
    busConfig.max_transfer_sz = (kSendBufferPixels * static_cast<int>(sizeof(uint16_t))) + 8;
    busConfig.flags = SPICOMMON_BUSFLAG_MASTER | SPICOMMON_BUSFLAG_GPIO_PINS;

    spi_device_interface_config_t deviceConfig = {};
    deviceConfig.command_bits = 8;
    deviceConfig.address_bits = 24;
    deviceConfig.mode = SPI_MODE3;
    deviceConfig.clock_speed_hz = kSpiFrequency;
    deviceConfig.spics_io_num = BoardConfig::PIN_LCD_CS;
    deviceConfig.flags = SPI_DEVICE_HALFDUPLEX;
    // Queue depth must accommodate the largest single PushColorsBegin call —
    // worst case is a full-frame push (~7 pixel chunks) plus headroom. Matches
    // kMaxPendingTxns above.
    deviceConfig.queue_size = 12;

    ESP_ERROR_CHECK(spi_bus_initialize(SPI3_HOST, &busConfig, SPI_DMA_CH_AUTO));
    ESP_ERROR_CHECK(spi_bus_add_device(SPI3_HOST, &deviceConfig, &gSpi));
    gBusReady = true;
  }

  for (const auto &command : kQspiInit) {
    sendCommand(command.cmd, command.data, command.len);
    if (command.delayMs != 0) {
      delay(command.delayMs);
    }
  }

  ESP_LOGI(kAxs15231bTag, "AXS15231B QSPI init complete");
}

void axs15231bSetBacklight(bool on) { setBacklight(on); }

void axs15231bSetBrightnessPercent(uint8_t percent) {
  if (percent == 0) {
    percent = 1;
  } else if (percent > 100) {
    percent = 100;
  }

  gBrightnessPercent = percent;
  writeBacklightPwm();
}

void axs15231bSleep() {
  // The panel can wake to a lit-but-blank state after AXS15231B SLPIN on this board.
  // For light sleep, blank the frame before this call and only switch off the backlight.
  setBacklight(false);
}

void axs15231bWake() {
  sendCommand(0x11, nullptr, 0);
  delay(100);
  sendCommand(0x29, nullptr, 0);
  setBacklight(true);
}

void axs15231bPushColorsBegin(uint16_t x, uint16_t y, uint16_t width, uint16_t height,
                              const uint16_t *data) {
  if (gSpi == nullptr || data == nullptr || width == 0 || height == 0) {
    return;
  }

  // setColumnWindow stays synchronous (small / one transaction) and runs
  // BEFORE the chunk queue so the pixel transactions go to the right region.
  setColumnWindow(x, x + width - 1);

  bool firstSend = true;
  size_t pixelsRemaining = static_cast<size_t>(width) * height;
  const uint16_t *cursor = data;

  while (pixelsRemaining > 0) {
    if (gPendingCount >= kMaxPendingTxns) {
      // Safety drain — queue full. Pulls one completed transaction so we
      // don't overflow the pool. In normal operation we never hit this
      // because callers Begin → Wait per push.
      spi_transaction_t *finished;
      ESP_ERROR_CHECK(spi_device_get_trans_result(gSpi, &finished, portMAX_DELAY));
      --gPendingCount;
    }

    size_t chunkPixels = pixelsRemaining;
    if (chunkPixels > static_cast<size_t>(kSendBufferPixels)) {
      chunkPixels = kSendBufferPixels;
    }

    spi_transaction_ext_t &txn = gPendingTxns[gPendingHead];
    txn = {};
    if (firstSend) {
      txn.base.flags = SPI_TRANS_MODE_QIO;
      txn.base.cmd = 0x32;
      txn.base.addr = y == 0 ? 0x002C00 : 0x003C00;
      firstSend = false;
    } else {
      txn.base.flags =
          SPI_TRANS_MODE_QIO | SPI_TRANS_VARIABLE_CMD | SPI_TRANS_VARIABLE_ADDR |
          SPI_TRANS_VARIABLE_DUMMY;
      txn.command_bits = 0;
      txn.address_bits = 0;
      txn.dummy_bits = 0;
    }
    txn.base.tx_buffer = cursor;
    txn.base.length = chunkPixels * 16;

    ESP_ERROR_CHECK(spi_device_queue_trans(gSpi, &txn.base, portMAX_DELAY));
    gPendingHead = (gPendingHead + 1) % kMaxPendingTxns;
    ++gPendingCount;

    pixelsRemaining -= chunkPixels;
    cursor += chunkPixels;
  }
}

void axs15231bPushColorsWait() {
  while (gPendingCount > 0) {
    spi_transaction_t *finished;
    ESP_ERROR_CHECK(spi_device_get_trans_result(gSpi, &finished, portMAX_DELAY));
    --gPendingCount;
  }
}

// Drop-in synchronous variant. Same external contract as before — caller
// blocks until DMA completes — but now the wait yields to other FreeRTOS
// tasks via the semaphore in get_trans_result instead of busy-spinning in
// spi_device_polling_transmit. Storage worker / audio task / etc. make
// progress during display SPI.
void axs15231bPushColors(uint16_t x, uint16_t y, uint16_t width, uint16_t height,
                         const uint16_t *data) {
  axs15231bPushColorsBegin(x, y, width, height, data);
  axs15231bPushColorsWait();
}
