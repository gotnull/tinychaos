// OtaUpdater: pulls a published firmware image from the project's GitHub
// release page and self-flashes the ESP32-S3.
//
// Adapted from the rsvpnano OtaManager pattern but trimmed to MVP scope:
//
//   - No SD card config; uses the build-time TINYCHAOS_REPO_SLUG.
//   - Single GitHub repository, single asset name.
//   - Latest-release polling via api.github.com (User-Agent header required).
//   - Asset name is fixed: "tinychaos-esp32s3.bin".
//   - HTTPS only, fetched via WiFiClientSecure + setInsecure() to avoid
//     bundling a cert chain in flash. The download URL is the API-resolved
//     signed objects.githubusercontent.com URL; no redirect chasing needed.
//
// Usage:
//   OtaUpdater ota;
//   ota.checkLatest();           // populates latestTag() / hasUpdate()
//   if (ota.hasUpdate()) ota.applyUpdate();   // reboots on success

#pragma once

#include <Arduino.h>

#ifndef TINYCHAOS_REPO_SLUG
#define TINYCHAOS_REPO_SLUG "gotnull/tinychaos"
#endif

#ifndef TINYCHAOS_OTA_ASSET_NAME
#define TINYCHAOS_OTA_ASSET_NAME "tinychaos-esp32s3.bin"
#endif

class OtaUpdater {
 public:
  // Hit api.github.com/repos/<slug>/releases/latest and record the
  // tag_name + signed asset URL. Returns true on success.
  bool checkLatest();

  // Pull the resolved asset URL and stream it through Update.* into the
  // OTA partition. On success this calls ESP.restart() and never returns.
  // On failure returns false and leaves lastError() populated.
  bool applyUpdate();

  // After a successful checkLatest(), true if latestTag() differs from
  // the running build tag (TINYCHAOS_BUILD_TAG burned in at compile time).
  bool hasUpdate() const;

  const String &latestTag() const { return latestTag_; }
  const String &latestAssetUrl() const { return latestAssetUrl_; }
  const String &lastError() const { return lastError_; }

  // Progress feedback. Used by the device's status page so the user can
  // see download progress instead of an opaque spinner.
  size_t bytesWritten() const { return bytesWritten_; }
  size_t bytesTotal() const { return bytesTotal_; }
  bool isBusy() const { return busy_; }

  // Compile-time constants exposed so the web UI can show them.
  static const char *runningBuildTag() { return TINYCHAOS_BUILD_TAG; }
  static const char *repoSlug() { return TINYCHAOS_REPO_SLUG; }
  static const char *assetName() { return TINYCHAOS_OTA_ASSET_NAME; }

 private:
  String latestTag_;
  String latestAssetUrl_;
  String lastError_;
  size_t bytesWritten_ = 0;
  size_t bytesTotal_ = 0;
  bool   busy_ = false;
};
