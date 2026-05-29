// tinychaos display implementation.
//
// The pixel pipeline and tiny-font rasterisation are lifted from
// rsvpnano/src/display/DisplayManager.cpp (panelColor swap, kTinyGlyphs
// table, drawTinyGlyph family, virtualFrame native-stripe transpose, dual
// stripe-buffer DMA flush). Keeping them byte-identical means the menu lines
// up pixel-for-pixel with rsvpnano's compact menus on the same panel.

#include "display/Display.h"

#include <algorithm>
#include <cstring>

#include <esp_heap_caps.h>
#include <esp_log.h>

#include "board/BoardConfig.h"
#include "display/axs15231b.h"

namespace {

constexpr int kDisplayWidth       = BoardConfig::DISPLAY_WIDTH;
constexpr int kDisplayHeight      = BoardConfig::DISPLAY_HEIGHT;
constexpr int kPanelNativeWidth   = BoardConfig::PANEL_NATIVE_WIDTH;
constexpr int kPanelNativeHeight  = BoardConfig::PANEL_NATIVE_HEIGHT;

constexpr int kTinyGlyphWidth   = 5;
constexpr int kTinyGlyphHeight  = 7;
constexpr int kTinyGlyphSpacing = 1;
constexpr int kTinyScale        = 2;

constexpr int kCompactMenuRowHeight = 22;
constexpr int kCompactMenuX         = 28;
constexpr int kFooterMarginX        = 12;

constexpr uint16_t kBgBlack      = 0x0000;
constexpr uint16_t kWhite        = 0xFFFF;
constexpr uint16_t kMenuDimColor = 0x8410;  // ~gray
constexpr uint16_t kFocusRed     = 0xF800;  // rsvpnano selected/focus
constexpr uint16_t kChipBg       = 0x18E3;  // muted dark chip
constexpr uint16_t kChipText     = 0xCE79;

constexpr size_t kBytesPerPixel  = sizeof(uint16_t);
constexpr size_t kMaxChunkBytes  = 16 * 1024;
constexpr int    kTxBufferWidth  = kPanelNativeWidth;
constexpr int    kMaxChunkPhysicalRows =
    static_cast<int>(kMaxChunkBytes / (kTxBufferWidth * kBytesPerPixel));
static_assert(kMaxChunkPhysicalRows > 0, "Chunk buffer must hold at least one row");
constexpr size_t kTxBufferPixels =
    static_cast<size_t>(kTxBufferWidth) * kMaxChunkPhysicalRows;

constexpr const char *kDisplayTag = "Display";

// 5x7 bitmap font, lifted verbatim from rsvpnano kTinyGlyphs[]. Each row's
// 5 LSBs are pixels (MSB-first when iterated with `1 << (kTinyGlyphWidth-1-col)`).
struct TinyGlyph {
  char    c;
  uint8_t rows[kTinyGlyphHeight];
};

constexpr TinyGlyph kTinyGlyphs[] = {
    {' ', {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}},
    {'!', {0x04, 0x04, 0x04, 0x04, 0x04, 0x00, 0x04}},
    {'"', {0x0A, 0x0A, 0x0A, 0x00, 0x00, 0x00, 0x00}},
    {'#', {0x0A, 0x0A, 0x1F, 0x0A, 0x1F, 0x0A, 0x0A}},
    {'%', {0x19, 0x19, 0x02, 0x04, 0x08, 0x13, 0x13}},
    {'&', {0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D}},
    {'\'', {0x04, 0x04, 0x08, 0x00, 0x00, 0x00, 0x00}},
    {'(', {0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02}},
    {')', {0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08}},
    {'*', {0x00, 0x15, 0x0E, 0x1F, 0x0E, 0x15, 0x00}},
    {'+', {0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00}},
    {',', {0x00, 0x00, 0x00, 0x00, 0x06, 0x04, 0x08}},
    {'-', {0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00}},
    {'.', {0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C}},
    {'/', {0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10}},
    {'0', {0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E}},
    {'1', {0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E}},
    {'2', {0x0E, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1F}},
    {'3', {0x1E, 0x01, 0x01, 0x0E, 0x01, 0x01, 0x1E}},
    {'4', {0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02}},
    {'5', {0x1F, 0x10, 0x10, 0x1E, 0x01, 0x01, 0x1E}},
    {'6', {0x0E, 0x10, 0x10, 0x1E, 0x11, 0x11, 0x0E}},
    {'7', {0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08}},
    {'8', {0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E}},
    {'9', {0x0E, 0x11, 0x11, 0x0F, 0x01, 0x01, 0x0E}},
    {':', {0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00}},
    {';', {0x00, 0x0C, 0x0C, 0x00, 0x06, 0x04, 0x08}},
    {'?', {0x0E, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04}},
    {'>', {0x10, 0x08, 0x04, 0x02, 0x04, 0x08, 0x10}},
    {'A', {0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11}},
    {'B', {0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E}},
    {'C', {0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E}},
    {'D', {0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E}},
    {'E', {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F}},
    {'F', {0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10}},
    {'G', {0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F}},
    {'H', {0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11}},
    {'I', {0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E}},
    {'J', {0x07, 0x02, 0x02, 0x02, 0x12, 0x12, 0x0C}},
    {'K', {0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11}},
    {'L', {0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F}},
    {'M', {0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11}},
    {'N', {0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11}},
    {'O', {0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E}},
    {'P', {0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10}},
    {'Q', {0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D}},
    {'R', {0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11}},
    {'S', {0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E}},
    {'T', {0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04}},
    {'U', {0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E}},
    {'V', {0x11, 0x11, 0x11, 0x11, 0x0A, 0x0A, 0x04}},
    {'W', {0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11}},
    {'X', {0x11, 0x0A, 0x04, 0x04, 0x04, 0x0A, 0x11}},
    {'Y', {0x11, 0x0A, 0x04, 0x04, 0x04, 0x04, 0x04}},
    {'Z', {0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F}},
    {'_', {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F}},
};

const uint8_t *tinyRowsFor(char c) {
  if (c >= 'a' && c <= 'z') c = static_cast<char>(c - 'a' + 'A');
  for (const TinyGlyph &g : kTinyGlyphs) if (g.c == c) return g.rows;
  for (const TinyGlyph &g : kTinyGlyphs) if (g.c == ' ') return g.rows;
  return kTinyGlyphs[0].rows;
}

uint16_t panelColor(uint16_t rgb565) {
  // Panel expects byte-swapped RGB565 over SPI.
  return static_cast<uint16_t>((rgb565 << 8) | (rgb565 >> 8));
}

uint16_t blendRgb565(uint16_t fg, uint16_t bg, uint8_t alpha) {
  if (alpha >= 250) return fg;
  if (alpha == 0)   return bg;
  const uint32_t inv = 255U - alpha;
  const uint32_t r = ((((fg >> 11) & 0x1FU) * alpha) + (((bg >> 11) & 0x1FU) * inv)) / 255U;
  const uint32_t g = ((((fg >>  5) & 0x3FU) * alpha) + (((bg >>  5) & 0x3FU) * inv)) / 255U;
  const uint32_t b = (((fg & 0x1FU)         * alpha) + ((bg & 0x1FU)         * inv)) / 255U;
  return static_cast<uint16_t>((r << 11) | (g << 5) | b);
}

// Marquee timing — matches rsvpnano: 60 px/s with 1.2 s pause at each edge.
constexpr int      kMarqueePixelsPerSecond = 60;
constexpr uint32_t kMarqueeEdgePauseMs     = 1200;

int marqueePingPongOffset(int maxOffset) {
  if (maxOffset <= 0) return 0;
  const uint32_t slideMs =
      static_cast<uint32_t>((maxOffset * 1000) / kMarqueePixelsPerSecond);
  const uint32_t cycleMs = (slideMs + kMarqueeEdgePauseMs) * 2;
  const uint32_t phase   = millis() % cycleMs;
  if (phase < kMarqueeEdgePauseMs) return 0;
  if (phase < kMarqueeEdgePauseMs + slideMs) {
    const uint32_t slidIn = phase - kMarqueeEdgePauseMs;
    return static_cast<int>((slidIn * maxOffset) / std::max<uint32_t>(1, slideMs));
  }
  if (phase < kMarqueeEdgePauseMs + slideMs + kMarqueeEdgePauseMs) return maxOffset;
  const uint32_t slidBack = phase - kMarqueeEdgePauseMs - slideMs - kMarqueeEdgePauseMs;
  return maxOffset -
         static_cast<int>((slidBack * maxOffset) / std::max<uint32_t>(1, slideMs));
}

}  // namespace

bool Display::begin() {
  ESP_LOGI(kDisplayTag, "Display::begin");
  if (!allocateBuffers()) {
    ESP_LOGE(kDisplayTag, "PSRAM/DMA allocation failed");
    return false;
  }
  axs15231bInit();
  initialized_   = true;
  lastRenderKey_ = "";
  fillScreenBlack();
  axs15231bSetBacklight(true);
  axs15231bSetBrightnessPercent(brightnessPercent_);
  return true;
}

bool Display::allocateBuffers() {
  if (virtualFrame_ == nullptr) {
    const size_t fbBytes =
        static_cast<size_t>(kDisplayWidth) * kDisplayHeight * sizeof(uint16_t);
    virtualFrame_ = static_cast<uint16_t *>(
        heap_caps_malloc(fbBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    if (virtualFrame_ == nullptr) {
      // PSRAM not available — fall back to internal RAM. 215 KB is tight but
      // possible on an ESP32-S3 R8 with no other heavy heap users.
      virtualFrame_ = static_cast<uint16_t *>(heap_caps_malloc(fbBytes, MALLOC_CAP_8BIT));
    }
  }
  if (txBuffer_ == nullptr) {
    txBuffer_ = static_cast<uint16_t *>(
        heap_caps_malloc(kTxBufferPixels * sizeof(uint16_t),
                         MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL));
  }
  if (txBufferAlt_ == nullptr && txBuffer_ != nullptr) {
    txBufferAlt_ = static_cast<uint16_t *>(
        heap_caps_malloc(kTxBufferPixels * sizeof(uint16_t),
                         MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL));
    // Second buffer is optional — single-buffered flush still works if alloc fails.
  }
  return virtualFrame_ != nullptr && txBuffer_ != nullptr;
}

void Display::setBacklight(bool on) {
  if (initialized_) axs15231bSetBacklight(on);
}

void Display::setBrightnessPercent(uint8_t percent) {
  if (percent == 0) percent = 1;
  if (percent > 100) percent = 100;
  brightnessPercent_ = percent;
  if (initialized_) axs15231bSetBrightnessPercent(percent);
}

void Display::fillScreenBlack() {
  if (txBuffer_ == nullptr) return;
  const size_t pxPerChunk =
      static_cast<size_t>(kPanelNativeWidth) * kMaxChunkPhysicalRows;
  const uint16_t panelBlack = panelColor(kBgBlack);
  for (size_t i = 0; i < pxPerChunk; ++i) txBuffer_[i] = panelBlack;
  for (int yStart = 0; yStart < kPanelNativeHeight; yStart += kMaxChunkPhysicalRows) {
    const int rows = std::min(kMaxChunkPhysicalRows, kPanelNativeHeight - yStart);
    axs15231bPushColors(0, static_cast<uint16_t>(yStart),
                        static_cast<uint16_t>(kPanelNativeWidth),
                        static_cast<uint16_t>(rows), txBuffer_);
  }
}

void Display::clearVirtualBuffer() {
  if (virtualFrame_ == nullptr) return;
  const uint16_t panelBg = panelColor(kBgBlack);
  const size_t total = static_cast<size_t>(kDisplayWidth) * kDisplayHeight;
  std::fill_n(virtualFrame_, total, panelBg);
}

void Display::fillVirtualRect(int x, int y, int width, int height, uint16_t color) {
  if (virtualFrame_ == nullptr) return;
  const uint16_t panel = panelColor(color);
  const int xEnd = std::min(kDisplayWidth, x + width);
  const int yEnd = std::min(kDisplayHeight, y + height);
  x = std::max(0, x);
  y = std::max(0, y);
  for (int row = y; row < yEnd; ++row) {
    uint16_t *line = virtualFrame_ + row * kDisplayWidth;
    for (int col = x; col < xEnd; ++col) line[col] = panel;
  }
}

int Display::measureTinyTextWidth(const String &text, int scale) const {
  if (text.isEmpty()) return 0;
  return static_cast<int>(text.length()) * (kTinyGlyphWidth + kTinyGlyphSpacing) * scale -
         kTinyGlyphSpacing * scale;
}

String Display::fitTinyText(const String &text, int maxWidth, int scale) const {
  if (measureTinyTextWidth(text, scale) <= maxWidth) return text;
  String fitted = text;
  const String ellipsis = "...";
  while (!fitted.isEmpty() &&
         measureTinyTextWidth(fitted + ellipsis, scale) > maxWidth) {
    fitted.remove(fitted.length() - 1);
  }
  fitted.trim();
  return fitted.isEmpty() ? ellipsis : fitted + ellipsis;
}

void Display::drawTinyGlyph(int x, int y, char c, uint16_t color, int scale) {
  if (virtualFrame_ == nullptr) return;
  const uint8_t *rows = tinyRowsFor(c);
  const uint16_t panel = panelColor(color);
  for (int row = 0; row < kTinyGlyphHeight; ++row) {
    for (int col = 0; col < kTinyGlyphWidth; ++col) {
      if ((rows[row] & (1 << (kTinyGlyphWidth - 1 - col))) == 0) continue;
      for (int yy = 0; yy < scale; ++yy) {
        const int dstY = y + row * scale + yy;
        if (dstY < 0 || dstY >= kDisplayHeight) continue;
        for (int xx = 0; xx < scale; ++xx) {
          const int dstX = x + col * scale + xx;
          if (dstX < 0 || dstX >= kDisplayWidth) continue;
          virtualFrame_[dstY * kDisplayWidth + dstX] = panel;
        }
      }
    }
  }
}

void Display::drawTinyGlyphClipped(int x, int y, char c, uint16_t color, int scale,
                                   int clipLeftX, int clipRightX) {
  if (virtualFrame_ == nullptr) return;
  const uint8_t *rows = tinyRowsFor(c);
  const uint16_t panel = panelColor(color);
  for (int row = 0; row < kTinyGlyphHeight; ++row) {
    for (int col = 0; col < kTinyGlyphWidth; ++col) {
      if ((rows[row] & (1 << (kTinyGlyphWidth - 1 - col))) == 0) continue;
      for (int yy = 0; yy < scale; ++yy) {
        const int dstY = y + row * scale + yy;
        if (dstY < 0 || dstY >= kDisplayHeight) continue;
        for (int xx = 0; xx < scale; ++xx) {
          const int dstX = x + col * scale + xx;
          if (dstX < 0 || dstX >= kDisplayWidth) continue;
          if (dstX < clipLeftX || dstX >= clipRightX) continue;
          virtualFrame_[dstY * kDisplayWidth + dstX] = panel;
        }
      }
    }
  }
}

void Display::drawTinyGlyphFaded(int x, int y, char c, uint16_t color, int scale,
                                 int clipLeftX, int clipRightX, int fadeWidth,
                                 uint16_t fadeColor) {
  if (virtualFrame_ == nullptr) return;
  const uint8_t *rows = tinyRowsFor(c);
  for (int row = 0; row < kTinyGlyphHeight; ++row) {
    for (int col = 0; col < kTinyGlyphWidth; ++col) {
      if ((rows[row] & (1 << (kTinyGlyphWidth - 1 - col))) == 0) continue;
      for (int yy = 0; yy < scale; ++yy) {
        const int dstY = y + row * scale + yy;
        if (dstY < 0 || dstY >= kDisplayHeight) continue;
        for (int xx = 0; xx < scale; ++xx) {
          const int dstX = x + col * scale + xx;
          if (dstX < 0 || dstX >= kDisplayWidth) continue;
          if (dstX < clipLeftX || dstX >= clipRightX) continue;
          uint8_t alpha = 255;
          if (fadeWidth > 0) {
            const int distLeft  = dstX - clipLeftX;
            const int distRight = clipRightX - 1 - dstX;
            const int edge      = std::min(distLeft, distRight);
            if (edge < fadeWidth) {
              const int a = (edge * 255) / std::max(1, fadeWidth);
              alpha = static_cast<uint8_t>(std::max(0, std::min(255, a)));
            }
          }
          virtualFrame_[dstY * kDisplayWidth + dstX] =
              panelColor(blendRgb565(color, fadeColor, alpha));
        }
      }
    }
  }
}

void Display::drawTinyTextAt(const String &text, int x, int y, uint16_t color, int scale) {
  int cursorX = x;
  for (size_t i = 0; i < text.length(); ++i) {
    drawTinyGlyph(cursorX, y, text[i], color, scale);
    cursorX += (kTinyGlyphWidth + kTinyGlyphSpacing) * scale;
  }
}

void Display::drawTinyTextCentered(const String &text, int y, uint16_t color, int scale) {
  const int textWidth = measureTinyTextWidth(text, scale);
  drawTinyTextAt(text, std::max(0, (kDisplayWidth - textWidth) / 2), y, color, scale);
}

void Display::drawTinyMarquee(const String &text, int leftX, int rightX, int textY,
                              uint16_t color, uint16_t fadeColor) {
  if (text.isEmpty() || rightX <= leftX) return;
  const int maxWidth  = rightX - leftX;
  const int textWidth = measureTinyTextWidth(text, kTinyScale);
  if (textWidth <= maxWidth) {
    drawTinyTextAt(text, leftX, textY, color, kTinyScale);
    return;
  }
  const int charPitch    = (kTinyGlyphWidth + kTinyGlyphSpacing) * kTinyScale;
  const int charBodyW    = kTinyGlyphWidth * kTinyScale;
  const int maxOffset    = textWidth - maxWidth;
  const int offsetPx     = marqueePingPongOffset(maxOffset);
  const int fadeWidthPx  = std::min<int>(10, maxWidth / 5);
  for (size_t ci = 0; ci < text.length(); ++ci) {
    const int charX     = leftX - offsetPx + static_cast<int>(ci) * charPitch;
    const int charRight = charX + charBodyW;
    if (charX >= rightX) break;
    if (charRight <= leftX) continue;
    drawTinyGlyphFaded(charX, textY, text[ci], color, kTinyScale,
                       leftX, rightX, fadeWidthPx, fadeColor);
  }
}

void Display::drawStatusChip() {
  if (statusChip_.isEmpty()) return;
  const int chipPadX  = 6;
  const int chipPadY  = 3;
  const int textW     = measureTinyTextWidth(statusChip_, kTinyScale);
  const int chipW     = textW + chipPadX * 2;
  const int chipH     = kTinyGlyphHeight * kTinyScale + chipPadY * 2;
  const int chipX     = kDisplayWidth - chipW - kFooterMarginX;
  const int chipY     = 6;
  fillVirtualRect(chipX, chipY, chipW, chipH, kChipBg);
  drawTinyTextAt(statusChip_, chipX + chipPadX, chipY + chipPadY,
                 kChipText, kTinyScale);
}

void Display::renderMenu(const std::vector<String> &items, size_t selectedIndex,
                         const std::vector<bool> &chevronRows) {
  if (!initialized_ || virtualFrame_ == nullptr) return;
  if (items.empty()) {
    renderCenteredLine("MENU");
    return;
  }
  if (selectedIndex >= items.size()) selectedIndex = items.size() - 1;

  // Cache-key the render so static frames don't repaint at 60 fps. Marquee
  // scrolling on the selected row forces a repaint via wall-clock millis().
  String renderKey = "menu|";
  renderKey += String(static_cast<unsigned>(selectedIndex));
  renderKey += "|c:";
  renderKey += statusChip_;
  for (const String &item : items) { renderKey += "|"; renderKey += item; }
  for (size_t i = 0; i < chevronRows.size(); ++i) {
    if (chevronRows[i]) { renderKey += "|cr"; renderKey += String(static_cast<unsigned>(i)); }
  }
  // Marquee on the selected row needs continuous repaints; include the
  // current marquee phase so renderKey changes every frame whenever the
  // selected item would scroll.
  const int selectedW = measureTinyTextWidth(items[selectedIndex], kTinyScale);
  const int rowMaxW   = kDisplayWidth - kCompactMenuX - kFooterMarginX - 20;
  if (selectedW > rowMaxW) {
    renderKey += "|t:";
    renderKey += String(static_cast<unsigned long>(millis() / 33));  // ~30 fps marquee step
  }
  if (renderKey == lastRenderKey_) return;
  lastRenderKey_ = renderKey;

  const int virtualHeight = kDisplayHeight;
  const size_t itemCount  = items.size();
  const size_t visibleCount =
      std::min(itemCount,
               static_cast<size_t>(std::max(1, virtualHeight / kCompactMenuRowHeight)));
  size_t firstVisible = 0;
  if (selectedIndex >= visibleCount / 2) firstVisible = selectedIndex - visibleCount / 2;
  if (firstVisible + visibleCount > itemCount) firstVisible = itemCount - visibleCount;

  const int rowHeight = kCompactMenuRowHeight;
  int y = 0;

  clearVirtualBuffer();

  const int chevronWidth = measureTinyTextWidth(">", kTinyScale);
  const int spaceWidth   = (kTinyGlyphWidth + kTinyGlyphSpacing) * kTinyScale;

  int maxItemTextWidth = 0;
  for (size_t i = 0; i < itemCount; ++i) {
    const int w = measureTinyTextWidth(items[i], kTinyScale);
    if (w > maxItemTextWidth) maxItemTextWidth = w;
  }
  const int chevronCapX = kDisplayWidth - chevronWidth - kFooterMarginX - 4;
  const int chevronColumnX =
      std::min(kCompactMenuX + maxItemTextWidth + spaceWidth * 2, chevronCapX);

  for (size_t row = 0; row < visibleCount; ++row) {
    const size_t itemIndex = firstVisible + row;
    const bool   selected  = itemIndex == selectedIndex;
    const uint16_t color   = selected ? kFocusRed : kMenuDimColor;
    const bool   hasChev   =
        itemIndex < chevronRows.size() && chevronRows[itemIndex];
    const int    maxWidth  =
        std::max(0, chevronColumnX - kCompactMenuX - spaceWidth);

    if (selected) {
      fillVirtualRect(10, y + 2, 5, kTinyGlyphHeight * kTinyScale + 2, kFocusRed);
    }
    const String &itemText      = items[itemIndex];
    const int    itemTextWidth  = measureTinyTextWidth(itemText, kTinyScale);
    const int    textY          = y + 3;
    const int    leftX          = kCompactMenuX;
    const int    rightX         = leftX + maxWidth;

    if (itemTextWidth <= maxWidth) {
      drawTinyTextAt(itemText, leftX, textY, color, kTinyScale);
    } else if (selected) {
      drawTinyMarquee(itemText, leftX, rightX, textY, color, kBgBlack);
    } else {
      const String fitted = fitTinyText(itemText, maxWidth, kTinyScale);
      drawTinyTextAt(fitted, leftX, textY, color, kTinyScale);
    }
    if (hasChev) drawTinyTextAt(">", chevronColumnX, textY, color, kTinyScale);

    y += rowHeight;
  }

  drawStatusChip();
  flushFrame();
}

void Display::renderCenteredLine(const String &line) {
  if (!initialized_ || virtualFrame_ == nullptr) return;
  const String renderKey = String("center|") + line + "|" + statusChip_;
  if (renderKey == lastRenderKey_) return;
  lastRenderKey_ = renderKey;

  clearVirtualBuffer();
  const int textY =
      std::max(0, (kDisplayHeight - kTinyGlyphHeight * kTinyScale) / 2);
  drawTinyTextCentered(line, textY, kWhite, kTinyScale);
  drawStatusChip();
  flushFrame();
}

void Display::renderStatusCard(const String &title, const String &line1,
                               const String &line2) {
  if (!initialized_ || virtualFrame_ == nullptr) return;
  const String renderKey = String("status|") + title + "|" + line1 + "|" + line2 +
                           "|" + statusChip_;
  if (renderKey == lastRenderKey_) return;
  lastRenderKey_ = renderKey;

  clearVirtualBuffer();

  const int titleY = 36;
  drawTinyTextCentered(title, titleY, kFocusRed, kTinyScale);

  const int line1Y = titleY + (kTinyGlyphHeight * kTinyScale) + 20;
  drawTinyTextCentered(line1, line1Y, kWhite, kTinyScale);

  const int line2Y = line1Y + (kTinyGlyphHeight * kTinyScale) + 8;
  drawTinyTextCentered(line2, line2Y, kMenuDimColor, kTinyScale);

  drawStatusChip();
  flushFrame();
}

void Display::flushFrame() {
  if (virtualFrame_ == nullptr || txBuffer_ == nullptr) return;

  // Native panel is 172 wide x 640 tall (portrait); we draw into a 640 x 172
  // landscape virtual frame. The fast-path transpose rotates 90° clockwise:
  //   logicalY = kDisplayHeight - 1 - nativeX
  //   logicalX = nativeY
  // …packed in stripes of `kMaxChunkPhysicalRows` native rows each (47 on
  // the 16 KB DMA buffer), 14 stripes total per frame. Dual-buffer means we
  // compose stripe N+1 while DMA pushes stripe N.

  const bool dualBuffer = (txBufferAlt_ != nullptr);
  bool       pushPending = false;
  int        stripeIdx   = 0;

  for (int nativeYStart = 0; nativeYStart < kPanelNativeHeight;
       nativeYStart += kMaxChunkPhysicalRows, ++stripeIdx) {
    const int nativeRows = std::min(kMaxChunkPhysicalRows,
                                    kPanelNativeHeight - nativeYStart);
    const size_t chunkBytes =
        static_cast<size_t>(nativeRows) * kPanelNativeWidth * sizeof(uint16_t);
    uint16_t *composeBuf =
        (dualBuffer && (stripeIdx & 1)) ? txBufferAlt_ : txBuffer_;
    std::memset(composeBuf, 0, chunkBytes);

    for (int nativeX = 0; nativeX < kPanelNativeWidth; ++nativeX) {
      // 90 deg rotation, opposite handedness from rsvpnano's default.
      // rsvpnano uses uiRotated_=true (BOOT/PWR at top); we want the
      // "uiRotated_=false" mapping (BOOT/PWR at bottom) which is the
      // user's preferred orientation for tinychaos:
      //   logicalY = nativeX
      //   logicalX = kDisplayWidth - 1 - nativeY
      // This matches the !uiRotated_ branch of rsvpnano's
      // flushScaledFrame slow path verbatim.
      const int logicalY = nativeX;
      const uint16_t *srcRow = virtualFrame_ + logicalY * kDisplayWidth;
      uint16_t *dst = composeBuf + nativeX;
      for (int localY = 0; localY < nativeRows; ++localY) {
        const int srcX = kDisplayWidth - 1 - nativeYStart - localY;
        dst[localY * kPanelNativeWidth] = srcRow[srcX];
      }
    }

    if (pushPending) {
      axs15231bPushColorsWait();
      pushPending = false;
    }
    if (dualBuffer) {
      axs15231bPushColorsBegin(0, static_cast<uint16_t>(nativeYStart),
                               static_cast<uint16_t>(kPanelNativeWidth),
                               static_cast<uint16_t>(nativeRows), composeBuf);
      pushPending = true;
    } else {
      axs15231bPushColors(0, static_cast<uint16_t>(nativeYStart),
                          static_cast<uint16_t>(kPanelNativeWidth),
                          static_cast<uint16_t>(nativeRows), composeBuf);
    }
  }
  if (pushPending) axs15231bPushColorsWait();
}
