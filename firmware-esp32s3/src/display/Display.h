// tinychaos display: 640x172 panel renderer with rsvpnano-style menu output.
//
// This is the minimum subset of rsvpnano/src/display/DisplayManager.cpp that
// the tinychaos OTA UI needs: the 5x7 TinyGlyph bitmap font scaled 2x, a
// PSRAM-resident logical framebuffer, the native-stripe DMA flush, and the
// renderMenu() layout (compact rows, focusColor selected bar). Everything
// serif / RSVP / screensaver / demo / library / audio related is intentionally
// omitted — tinychaos doesn't need it.
//
// All public methods are safe to call before begin(); they short-circuit if
// the buffers haven't been allocated yet.
//
// Drop-in API:
//   Display d;
//   d.begin();
//   d.renderMenu({"Check for update", "Apply latest", "Back"}, selected, chevrons);

#pragma once

#include <Arduino.h>
#include <vector>

class Display {
 public:
  bool begin();
  bool isReady() const { return initialized_; }

  // Whole-screen primitives.
  void fillScreenBlack();
  void setBacklight(bool on);
  void setBrightnessPercent(uint8_t percent);

  // Top status chip drawn on every menu render (battery slot in rsvpnano;
  // we repurpose it for the running build tag so the user can always see
  // which version is on the device).
  void setStatusChip(const String &label) { statusChip_ = label; lastRenderKey_ = ""; }

  // Menu renderer — byte-identical layout to rsvpnano's compact menu:
  // 22 px row height, items left-aligned at x=28, selected row gets a red
  // 5 px focus bar at x=10..15 and the row text in red, unselected rows
  // dimmed. Long items truncate with "..." unless they are the selected
  // row, which marquees ping-pong with a soft edge fade.
  void renderMenu(const std::vector<String> &items, size_t selectedIndex,
                  const std::vector<bool> &chevronRows = {});

  // Center one line of tiny text. Used while booting and for short status
  // overlays (e.g. "Connecting to WiFi…").
  void renderCenteredLine(const String &line);

  // Three-line status card: large-ish title at the top, two muted lines
  // below. Used for OTA download progress + error reporting.
  void renderStatusCard(const String &title, const String &line1, const String &line2);

 private:
  bool allocateBuffers();
  void clearVirtualBuffer();
  void fillVirtualRect(int x, int y, int width, int height, uint16_t color);
  void drawTinyGlyph(int x, int y, char c, uint16_t color, int scale);
  void drawTinyGlyphClipped(int x, int y, char c, uint16_t color, int scale,
                            int clipLeftX, int clipRightX);
  void drawTinyGlyphFaded(int x, int y, char c, uint16_t color, int scale,
                          int clipLeftX, int clipRightX, int fadeWidth,
                          uint16_t fadeColor);
  void drawTinyTextAt(const String &text, int x, int y, uint16_t color, int scale);
  void drawTinyTextCentered(const String &text, int y, uint16_t color, int scale);
  void drawTinyMarquee(const String &text, int leftX, int rightX, int textY,
                       uint16_t color, uint16_t fadeColor);
  int  measureTinyTextWidth(const String &text, int scale) const;
  String fitTinyText(const String &text, int maxWidth, int scale) const;
  void drawStatusChip();
  void flushFrame();

  uint16_t *virtualFrame_ = nullptr;
  uint16_t *txBuffer_     = nullptr;
  uint16_t *txBufferAlt_  = nullptr;
  bool      initialized_  = false;
  uint8_t   brightnessPercent_ = 100;
  String    statusChip_;
  String    lastRenderKey_;
};
