// Pin map for the Waveshare ESP32-S3 R8 OPI dev board with QSPI AXS15231B
// panel (172 x 640 native, used in 640 x 172 landscape).
//
// Pulled verbatim from the rsvpnano BoardConfig so the panel driver and
// button handlers we copied work without modification. We only need the
// fields the display + button code references; the rest of rsvpnano's
// BoardConfig (battery / I2S audio / TCA9554 / SD MMC) is intentionally
// omitted for the tinychaos MVP.

#pragma once

#include <Arduino.h>

namespace BoardConfig {

constexpr int PIN_BOOT_BUTTON = 0;
constexpr int PIN_PWR_BUTTON  = 16;

constexpr int PIN_LCD_CS        = 9;
constexpr int PIN_LCD_SCLK      = 10;
constexpr int PIN_LCD_DATA0     = 11;
constexpr int PIN_LCD_DATA1     = 12;
constexpr int PIN_LCD_DATA2     = 13;
constexpr int PIN_LCD_DATA3     = 14;
constexpr int PIN_LCD_RST       = 21;
constexpr int PIN_LCD_BACKLIGHT = 8;

constexpr int PANEL_NATIVE_WIDTH  = 172;
constexpr int PANEL_NATIVE_HEIGHT = 640;
constexpr int DISPLAY_WIDTH       = 640;
constexpr int DISPLAY_HEIGHT      = 172;

}  // namespace BoardConfig
