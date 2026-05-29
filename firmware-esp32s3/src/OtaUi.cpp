#include "OtaUi.h"

#include <esp_log.h>

namespace {
constexpr const char *kTag = "OtaUi";
constexpr uint32_t kLongPressMs = 600;
}

void OtaUi::begin() {
  display_.setStatusChip(String("v ") + OtaUpdater::runningBuildTag());
  renderMessage("TINYCHAOS", "BOOTING", "");
}

bool OtaUi::consumeTapEvent() {
  const bool had = tapPending_;
  tapPending_ = false;
  return had;
}

void OtaUi::setWifiState(bool connected, const String &ssid, const String &ip) {
  wifiConnected_ = connected;
  wifiSsid_      = ssid;
  wifiIp_        = ip;
  // WiFi is on-demand now (off during normal streaming), so boot goes
  // straight to the menu — we do NOT sit in a "connecting to WiFi" screen.
  // The actual connect happens synchronously inside activateSelection()
  // (Check-for-update / Apply), which renders its own "CONNECTING WIFI"
  // status. Here we just leave Boot for Idle on the first status report.
  if (state_ == State::Boot) {
    state_ = State::Idle;
  }
}

void OtaUi::tick(uint32_t nowMs) {
  handleButton(nowMs);
  handleTouch(nowMs);

  // OtaUpdater::applyUpdate() reboots on success and never returns; the
  // only way to land back in tick() while downloading is mid-stream, so
  // the progress card is driven directly off OtaUpdater::bytesWritten()
  // when applyUpdate is invoked from another task. For now we drive it
  // synchronously from activateSelection() — the menu re-renders the
  // progress card before the call and again on every loop tick.

  switch (state_) {
    case State::Boot:
      renderMessage("TINYCHAOS", "BOOTING", "");
      return;
    case State::WifiConnecting:
      renderMessage("TINYCHAOS", "CONNECTING TO WIFI",
                    wifiSsid_.isEmpty() ? "" : wifiSsid_);
      return;
    case State::WifiFailed:
      renderMessage("TINYCHAOS",
                    "WIFI FAILED",
                    wifiSsid_.isEmpty() ? "" : wifiSsid_);
      return;
    case State::Downloading:
      renderProgress();
      return;
    case State::Rebooting:
      renderMessage("TINYCHAOS", "REBOOTING", "");
      return;
    case State::UpdateFailed:
      renderMessage("UPDATE FAILED",
                    lastErrorShown_,
                    "BOOT TO RETURN");
      return;
    default:
      break;
  }

  buildMenu();
  renderMain();
}

void OtaUi::handleButton(uint32_t nowMs) {
  button_.update(nowMs);
  if (state_ == State::UpdateFailed && button_.wasPressedEvent()) {
    state_ = State::Idle;
    return;
  }
  if (state_ != State::Idle && state_ != State::UpToDate &&
      state_ != State::UpdateAvailable && state_ != State::Checking) {
    return;
  }
  if (!button_.wasReleasedEvent()) return;
  const uint32_t held = button_.lastHoldDurationMs();
  if (held >= kLongPressMs) {
    activateSelection();
  } else {
    selected_ = nextSelectable(selected_ + 1);
  }
}

void OtaUi::handleTouch(uint32_t nowMs) {
  (void)nowMs;
  TouchEvent ev;
  if (!touch_.poll(ev)) return;

  // Any tap (anywhere on the screen) raises a tap event that main.cpp will
  // forward to the host via the next outgoing packet's FLAGS byte. The
  // host-side GUI uses this to auto-switch to its waveform tab.
  if (ev.phase == TouchPhase::End) {
    tapPending_ = true;
  }

  if (state_ == State::UpdateFailed) {
    if (ev.phase == TouchPhase::End) state_ = State::Idle;
    return;
  }
  if (state_ != State::Idle && state_ != State::UpToDate &&
      state_ != State::UpdateAvailable && state_ != State::Checking) {
    return;
  }

  if (ev.phase == TouchPhase::Start) {
    touchActive_  = true;
    touchStartY_  = ev.y;
    return;
  }

  if (ev.phase != TouchPhase::End || !touchActive_) return;
  touchActive_ = false;

  const int dy = static_cast<int>(ev.y) - static_cast<int>(touchStartY_);
  constexpr int kSwipeThresholdPx = 30;

  // Swipe vertically moves the menu selection. A tap (no significant
  // movement) snaps selection to the row under the finger and activates
  // it. (The tap-to-host event was already raised above; menu actions
  // and host signalling are independent.)
  if (dy <= -kSwipeThresholdPx) {
    selected_ = nextSelectable(selected_ + 1);
    return;
  }
  if (dy >= kSwipeThresholdPx) {
    if (!menu_.empty()) {
      size_t prev = (selected_ + menu_.size() - 1) % menu_.size();
      for (size_t step = 0; step < menu_.size(); ++step) {
        if (menu_[prev].selectable) { selected_ = prev; return; }
        prev = (prev + menu_.size() - 1) % menu_.size();
      }
    }
    return;
  }

  // Tap: convert release Y to a menu row (rsvpnano-style kCompactMenuRowHeight=22).
  constexpr int kRowH = 22;
  const size_t rowIdx = static_cast<size_t>(ev.y) / kRowH;
  if (rowIdx < menu_.size() && menu_[rowIdx].selectable) {
    selected_ = rowIdx;
    activateSelection();
  }
}

size_t OtaUi::nextSelectable(size_t from) const {
  if (menu_.empty()) return 0;
  for (size_t step = 0; step < menu_.size(); ++step) {
    const size_t i = (from + step) % menu_.size();
    if (menu_[i].selectable) return i;
  }
  return 0;
}

void OtaUi::buildMenu() {
  menu_.clear();
  const String runningLabel =
      String("RUNNING: ") + OtaUpdater::runningBuildTag();
  const String latestLabel =
      updater_.latestTag().isEmpty()
          ? String("LATEST: ?")
          : String("LATEST: ") + updater_.latestTag();
  String stateLabel;
  switch (state_) {
    case State::Idle:            stateLabel = "STATUS: IDLE"; break;
    case State::Checking:        stateLabel = "STATUS: CHECKING"; break;
    case State::UpToDate:        stateLabel = "STATUS: UP TO DATE"; break;
    case State::UpdateAvailable: stateLabel = "STATUS: UPDATE READY"; break;
    default:                     stateLabel = "STATUS: IDLE"; break;
  }
  const String wifiLabel =
      wifiConnected_
          ? (String("WIFI: ") + (wifiIp_.isEmpty() ? wifiSsid_ : wifiIp_))
          : String("WIFI: OFFLINE");

  menu_.push_back({runningLabel, Action::Info, false});
  menu_.push_back({latestLabel,  Action::Info, false});
  menu_.push_back({stateLabel,   Action::Info, false});
  menu_.push_back({wifiLabel,    Action::Info, false});

  if (state_ == State::Checking) {
    menu_.push_back({"CHECKING...", Action::Info, false});
  } else {
    menu_.push_back({"CHECK FOR UPDATE", Action::CheckForUpdate, true});
  }
  if (state_ == State::UpdateAvailable) {
    menu_.push_back({"APPLY UPDATE", Action::ApplyUpdate, true});
  }
  menu_.push_back({"REBOOT", Action::Reboot, true});

  if (!menu_[selected_].selectable) {
    selected_ = nextSelectable(0);
  }
}

void OtaUi::renderMain() {
  std::vector<String> labels;
  std::vector<bool>   chevrons;
  labels.reserve(menu_.size());
  chevrons.reserve(menu_.size());
  for (const MenuItem &m : menu_) {
    labels.push_back(m.label);
    chevrons.push_back(m.selectable);
  }
  display_.renderMenu(labels, selected_, chevrons);
}

void OtaUi::renderProgress() {
  const size_t total = updater_.bytesTotal();
  const size_t done  = updater_.bytesWritten();
  String title = "DOWNLOADING";
  String line1 = String(static_cast<unsigned>(done / 1024)) + " / " +
                 String(static_cast<unsigned>(total / 1024)) + " KB";
  String line2 = updater_.latestTag();
  renderMessage(title, line1, line2);
}

void OtaUi::renderMessage(const String &title, const String &line1,
                          const String &line2) {
  display_.renderStatusCard(title, line1, line2);
}

void OtaUi::activateSelection() {
  if (selected_ >= menu_.size()) return;
  const Action a = menu_[selected_].action;
  switch (a) {
    case Action::CheckForUpdate: {
      state_ = State::Checking;
      // WiFi is off during normal streaming; bring it up on demand. The
      // scan/connect generates ROM ets_printf noise on UART0, but that only
      // happens here, when the user explicitly asks to check for updates.
      if (!wifiConnected_) {
        renderMessage("CHECKING", "CONNECTING WIFI",
                      wifiSsid_.isEmpty() ? "" : wifiSsid_);
        if (!wifiConnectFn_ || !wifiConnectFn_()) {
          lastErrorShown_ = "WIFI CONNECT FAILED";
          state_          = State::UpdateFailed;
          return;
        }
        wifiConnected_ = true;
      }
      renderMessage("CHECKING", "FETCHING LATEST RELEASE",
                    OtaUpdater::repoSlug());
      const bool ok = updater_.checkLatest();
      if (!ok) {
        lastErrorShown_ = updater_.lastError();
        state_          = State::UpdateFailed;
        return;
      }
      state_       = updater_.hasUpdate() ? State::UpdateAvailable : State::UpToDate;
      lastCheckMs_ = millis();
      // Reselect "Apply update" so a follow-up long-press flashes immediately.
      if (state_ == State::UpdateAvailable) {
        buildMenu();
        for (size_t i = 0; i < menu_.size(); ++i) {
          if (menu_[i].action == Action::ApplyUpdate) { selected_ = i; break; }
        }
      }
      return;
    }
    case Action::ApplyUpdate: {
      if (!wifiConnected_) {
        renderMessage("APPLYING", "CONNECTING WIFI",
                      wifiSsid_.isEmpty() ? "" : wifiSsid_);
        if (!wifiConnectFn_ || !wifiConnectFn_()) {
          lastErrorShown_ = "WIFI CONNECT FAILED";
          state_          = State::UpdateFailed;
          return;
        }
        wifiConnected_ = true;
      }
      state_ = State::Downloading;
      renderProgress();
      const bool ok = updater_.applyUpdate();
      if (!ok) {
        lastErrorShown_ = updater_.lastError();
        state_          = State::UpdateFailed;
      }
      // On success, applyUpdate reboots and never returns. If it returns
      // false we fall through to the failed-state render on next tick.
      return;
    }
    case Action::Reboot:
      state_ = State::Rebooting;
      renderMessage("TINYCHAOS", "REBOOTING", "");
      delay(300);
      ESP.restart();
      return;
    case Action::Info:
      return;
  }
}
