// AXS15231B capacitive touch controller driver (I2C 0x3B), ported verbatim
// from rsvpnano/src/input/TouchHandler.{h,cpp}. The controller lives on
// the secondary I2C bus (PIN_TOUCH_SDA/SCL) on the Waveshare ESP32-S3 R8 OPI
// board.
//
// poll() reports Start/Move/End phases plus mapped (x, y) in logical screen
// coordinates respecting the uiRotated_ flag — set the flag to match the
// frame transpose in Display.cpp so touches land where you'd expect.

#pragma once

#include <Arduino.h>

enum class TouchPhase {
  Start,
  Move,
  End,
};

struct TouchEvent {
  bool       touched = false;
  uint16_t   x = 0;
  uint16_t   y = 0;
  uint8_t    gesture = 0;
  TouchPhase phase = TouchPhase::Move;
};

class TouchHandler {
 public:
  bool begin();
  void end();
  bool poll(TouchEvent &event);
  void cancel();
  void setUiRotated(bool rotated);

 private:
  static constexpr uint8_t kAddress = 0x3B;

  bool     initialized_              = false;
  bool     uiRotated_                = true;
  uint32_t lastPollMs_               = 0;
  uint32_t backoffUntilMs_           = 0;
  uint32_t lastTouchSampleMs_        = 0;
  uint8_t  consecutiveReadFailures_  = 0;
  uint8_t  emptyTouchSamples_        = 0;
  bool     touchActive_              = false;
  uint16_t lastX_                    = 0;
  uint16_t lastY_                    = 0;

  bool readTouchPacket(uint8_t *buffer, size_t len);
};
