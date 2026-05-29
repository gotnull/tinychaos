#include "OtaUpdater.h"

#include <HTTPClient.h>
#include <Update.h>
#include <WiFi.h>
#include <WiFiClientSecure.h>

namespace {

// Tiny string-only JSON extractor. The GitHub releases response is well
// known and only a handful of fields matter to us. Avoiding ArduinoJson
// keeps the binary smaller. If a field ever needs nested traversal we can
// upgrade to a real parser.
String extractStringField(const String &json, const char *key, int from = 0) {
  const String needle = String("\"") + key + "\":\"";
  const int keyIdx = json.indexOf(needle, from);
  if (keyIdx < 0) return "";
  const int start = keyIdx + needle.length();
  const int end = json.indexOf('"', start);
  if (end < 0) return "";
  return json.substring(start, end);
}

}  // namespace

bool OtaUpdater::checkLatest() {
  latestTag_ = "";
  latestAssetUrl_ = "";
  lastError_ = "";

  if (WiFi.status() != WL_CONNECTED) {
    lastError_ = "WiFi not connected";
    return false;
  }

  // /releases/latest excludes pre-releases — and our CI workflow publishes
  // every push to main as a pre-release, so /latest always 404s for us.
  // /releases?per_page=1 returns the most recent release (including
  // pre-releases) wrapped in a JSON array; we pluck the first object.
  const String apiUrl = String("https://api.github.com/repos/") + repoSlug() +
                        "/releases?per_page=1";

  WiFiClientSecure client;
  client.setInsecure();
  HTTPClient http;
  http.setTimeout(15000);
  http.setReuse(false);
  http.setFollowRedirects(HTTPC_FORCE_FOLLOW_REDIRECTS);
  // GitHub returns 403 if no User-Agent is set.
  http.setUserAgent("tinychaos-esp32s3/1.0");
  if (!http.begin(client, apiUrl)) {
    lastError_ = "http.begin failed";
    return false;
  }
  const int code = http.GET();
  if (code != HTTP_CODE_OK) {
    lastError_ = String("GitHub API HTTP ") + code;
    http.end();
    return false;
  }
  const String body = http.getString();
  http.end();

  latestTag_ = extractStringField(body, "tag_name");
  if (latestTag_.isEmpty()) {
    lastError_ = "tag_name missing from API response";
    return false;
  }

  // Find the assets array entry whose "name" matches our expected asset
  // file. The asset object contains "browser_download_url"; the value is
  // a signed objects.githubusercontent.com URL that fetches in one hop.
  const String nameKey = String("\"name\":\"") + assetName() + "\"";
  const int nameIdx = body.indexOf(nameKey);
  if (nameIdx < 0) {
    lastError_ = String("asset ") + assetName() + " not in release";
    return false;
  }
  latestAssetUrl_ = extractStringField(body, "browser_download_url", nameIdx);
  if (latestAssetUrl_.isEmpty()) {
    lastError_ = "browser_download_url missing for asset";
    return false;
  }

  return true;
}

bool OtaUpdater::hasUpdate() const {
  if (latestTag_.isEmpty()) return false;
  return latestTag_ != String(runningBuildTag());
}

bool OtaUpdater::applyUpdate() {
  lastError_ = "";
  bytesWritten_ = 0;
  bytesTotal_ = 0;
  busy_ = true;

  if (latestAssetUrl_.isEmpty()) {
    lastError_ = "no asset URL; call checkLatest() first";
    busy_ = false;
    return false;
  }
  if (WiFi.status() != WL_CONNECTED) {
    lastError_ = "WiFi not connected";
    busy_ = false;
    return false;
  }

  Serial.printf("[ota] applying update %s from %s\n",
                latestTag_.c_str(), latestAssetUrl_.c_str());

  WiFiClientSecure client;
  client.setInsecure();
  HTTPClient http;
  http.setTimeout(20000);
  http.setReuse(false);
  http.setFollowRedirects(HTTPC_FORCE_FOLLOW_REDIRECTS);
  http.setUserAgent("tinychaos-esp32s3/1.0");

  if (!http.begin(client, latestAssetUrl_)) {
    lastError_ = "http.begin failed";
    busy_ = false;
    return false;
  }
  const int code = http.GET();
  if (code != HTTP_CODE_OK) {
    lastError_ = String("download HTTP ") + code;
    http.end();
    busy_ = false;
    return false;
  }
  const int contentLength = http.getSize();
  if (contentLength <= 0) {
    lastError_ = "unknown content length";
    http.end();
    busy_ = false;
    return false;
  }
  bytesTotal_ = static_cast<size_t>(contentLength);

  if (!Update.begin(bytesTotal_)) {
    lastError_ = String("Update.begin: ") + Update.errorString();
    http.end();
    busy_ = false;
    return false;
  }

  WiFiClient *stream = http.getStreamPtr();
  uint8_t buf[1024];
  uint32_t lastReportMs = millis();
  while (http.connected() && bytesWritten_ < bytesTotal_) {
    const size_t available = stream->available();
    if (available > 0) {
      const int got = stream->readBytes(buf, std::min<size_t>(sizeof(buf), available));
      if (got > 0) {
        if (Update.write(buf, got) != static_cast<size_t>(got)) {
          lastError_ = String("Update.write: ") + Update.errorString();
          Update.abort();
          http.end();
          busy_ = false;
          return false;
        }
        bytesWritten_ += got;
        const uint32_t now = millis();
        if (now - lastReportMs >= 500) {
          Serial.printf("[ota] %u / %u KB\n",
                        static_cast<unsigned>(bytesWritten_ / 1024),
                        static_cast<unsigned>(bytesTotal_ / 1024));
          lastReportMs = now;
        }
      }
    } else {
      delay(2);
      yield();
    }
  }

  if (!Update.end(true)) {
    lastError_ = String("Update.end: ") + Update.errorString();
    http.end();
    busy_ = false;
    return false;
  }
  http.end();
  Serial.println("[ota] flash complete, rebooting");
  delay(500);
  ESP.restart();
  return true;  // unreachable
}
