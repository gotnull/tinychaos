// OtaUi: the on-device menu rendered on the AXS15231B TFT.
//
// Drives a Display through three screens — main menu, OTA progress card,
// and status messages — backed by a small state machine that wraps the
// existing OtaUpdater + WiFi state.
//
// Input model (single BOOT button, same as rsvpnano-style nav):
//   short press (<600 ms)  : move selection down (wraps)
//   long  press (>=600 ms) : activate the selected item
//
// State is read on every tick(); render() is called from loop() and uses
// the renderKey cache inside Display so static frames don't repaint.

#pragma once

#include <Arduino.h>
#include <vector>

#include "OtaUpdater.h"
#include "display/Display.h"
#include "input/ButtonHandler.h"
#include "input/TouchHandler.h"

class OtaUi {
 public:
  enum class State : uint8_t {
    Boot,
    WifiConnecting,
    WifiFailed,
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    UpdateFailed,
    Rebooting,
  };

  explicit OtaUi(Display &display, OtaUpdater &updater, ButtonHandler &button,
                 TouchHandler &touch)
      : display_(display), updater_(updater), button_(button), touch_(touch) {}

  void begin();
  void tick(uint32_t nowMs);

  // Status text caller sets once it knows the WiFi state. The UI doesn't try
  // to call WiFi.begin itself; the WiFi connection is owned by main.cpp.
  void setWifiState(bool connected, const String &ssid, const String &ip);

  State state() const { return state_; }

 private:
  enum class Action : uint8_t {
    CheckForUpdate,
    ApplyUpdate,
    Reboot,
    Info,
  };

  struct MenuItem {
    String label;
    Action action;
    bool   selectable;
  };

  void   handleButton(uint32_t nowMs);
  void   handleTouch(uint32_t nowMs);
  void   buildMenu();
  void   renderMain();
  void   renderProgress();
  void   renderMessage(const String &title, const String &line1, const String &line2);
  size_t nextSelectable(size_t from) const;
  void   activateSelection();

  Display       &display_;
  OtaUpdater    &updater_;
  ButtonHandler &button_;
  TouchHandler  &touch_;

  uint16_t touchStartY_   = 0;
  bool     touchActive_   = false;

  State  state_ = State::Boot;
  String wifiSsid_;
  String wifiIp_;
  bool   wifiConnected_ = false;
  String lastErrorShown_;
  size_t selected_      = 0;
  std::vector<MenuItem> menu_;

  uint32_t lastCheckMs_     = 0;
  uint32_t lastProgressMs_  = 0;
};
