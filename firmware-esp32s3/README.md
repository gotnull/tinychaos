# tinychaos firmware (ESP32-S3)

PlatformIO project for the Waveshare ESP32-S3 R8 OPI dev board (or any board that matches the `esp32-s3-r8-opi` profile). Streams framed binary packets to the host over USB CDC, with WiFi + ArduinoOTA + ElegantOTA so you can push new firmware over the network once the device is on your WiFi.

This is the **fastest path to a working end-to-end demo on hardware you already own**. The wire format is byte-for-byte identical to the STM32 firmware in [../firmware/](../firmware/), so all the host tooling (GUI, Python CLI, C# CLI, replay, recording) works unchanged.

## What this MVP does and what it does not

In:

- USB CDC streaming of framed packets at 20 kHz aggregate (one channel, ADC1_CH0 / GPIO 1).
- WiFi station mode (one SSID/password from a header file).
- ArduinoOTA (push firmware from PlatformIO via WiFi: `pio run -t upload --upload-port <device-ip>`).
- ElegantOTA web upload form at `http://<device-ip>/update` for browser-based updates.
- A tiny status page at `http://<device-ip>/` showing build tag, uptime, RSSI, packet sequence.
- Build-tag injection (UTC timestamp embedded in the binary, useful for diagnosing stale-OTA).

Out (intentional MVP cut):

- Captive portal / WiFi credential management. Edit `wifi_config.h` and reflash to change networks.
- Multiple WiFi networks. Picks one and tries it.
- TCP streaming. USB CDC only. Adding TCP would be a ~30 line patch using AsyncTCP.
- GitHub-release-based auto-update (rsvpnano does this; overkill for our MVP).
- Two-channel sampling. The host expects 2 channels by default but accepts 1 channel via `--channels 1` on the CLI or the corresponding GUI setting. Multi-channel is a straightforward extension of the analogContinuous call.
- Spike encoding. The `FLAGS` byte's bit 0 is reserved for it; firmware is still in raw-sample mode.

## Prerequisites

- **PlatformIO Core**. Install via `pipx install platformio` or use the PlatformIO VS Code extension.
- A Waveshare ESP32-S3 R8 OPI board (or compatible: ESP32-S3 with native USB, 16 MB flash, 8 MB octal PSRAM).

`pio --version` should print something. Once it does, the rest is one command.

## First-time setup

```
cd firmware-esp32s3
cp src/wifi_config.h.template src/wifi_config.h
$EDITOR src/wifi_config.h
```

Fill in `WIFI_SSID` and `WIFI_PASSWORD`. Optional: change `TINYCHAOS_HOSTNAME` (default `tinychaos`) and set `OTA_PASSWORD` if you want ArduinoOTA / ElegantOTA to require auth.

If you leave `WIFI_SSID` empty the firmware skips WiFi entirely and just streams over USB. That is enough for first bring-up; add WiFi when you want OTA.

## Build and upload over USB (first time)

Plug the board in via USB. On macOS and Linux it should enumerate automatically. On Windows you may need the CP210x / CH343 / native USB-Serial driver depending on which USB chip your board uses (the Waveshare ESP32-S3 R8 OPI uses native USB, no extra driver).

```
pio run -e waveshare_esp32s3 -t upload
pio device monitor -e waveshare_esp32s3
```

The monitor speed is 921600. You should see:

```
[boot] tinychaos esp32-s3, build=YYMMDD-HHMMSS
[wifi] connecting to '<ssid>'...
[wifi] connected, ip=10.0.0.42 rssi=-52
[ota] ArduinoOTA listening on tinychaos.local:3232
[web] http://10.0.0.42/ (status), http://10.0.0.42/update (ElegantOTA)
[adc] continuous on GPIO1 @ 20000 Hz, 256 samples/batch
[boot] streaming packets to USB CDC
```

The board now appears as `/dev/tty.usbmodem*` (macOS), `COMx` (Windows), or `/dev/ttyACM0` (Linux). Open the host GUI (`dotnet run --project ../analysis/src/TinyChaos.Gui -c Release`) or the Python CLI (`python -m tinychaos.cli --port <port>` from `../tools/`) and connect. You should see live data.

Touch a wire from GPIO 1 to ground, or to 3.3 V, or just touch it with your finger and watch 50 Hz pickup show up on the WAVEFORM panel.

## Subsequent updates over the air

Three independent OTA paths ship in this firmware. Pick whichever matches the situation.

### A. Hands-free: GitHub release pull (recommended)

The repo has a GitHub Actions workflow (`.github/workflows/firmware-esp32s3-release.yml`) that builds the firmware on every push to `main` and publishes a release with `tinychaos-esp32s3.bin` attached. On boot, the device queries `api.github.com/repos/gotnull/tinychaos/releases/latest`, compares the release's `tag_name` against the burned-in `TINYCHAOS_BUILD_TAG`, and exposes the result on its web page. To flash the latest:

1. Open `http://<device-ip>/` on your phone or laptop (same WiFi as the device).
2. The page shows `running: <local-tag>` and `latest: <published-tag>`, with an "update available" badge when they differ.
3. Click **Update now**. The device fetches `tinychaos-esp32s3.bin` straight from the release's signed `objects.githubusercontent.com` URL, streams it through the ESP32 OTA `Update` API, and reboots into the new build.

No USB, no PlatformIO, no esptool, no BOOT button. The whole flow takes ~10 seconds on a typical home network.

For scripted checks:

- `GET http://<device-ip>/ota/status` returns JSON: running, latest, asset_url, has_update, last_error, busy, bytes_written, bytes_total.
- `GET http://<device-ip>/ota/check` re-runs the API check (refreshes the cached latest).
- `GET http://<device-ip>/ota/apply` triggers the download and flash (returns immediately; the actual work happens on the main loop).

### B. Push a local build directly: ArduinoOTA from PlatformIO

If you have a local build you want to flash before publishing a release:

```
pio run -e waveshare_esp32s3 -t upload --upload-port <device-ip>
```

This uses ArduinoOTA on port 3232. Requires the device be on the same network and the firmware running (so it is listening). If you set `OTA_PASSWORD` in `wifi_config.h`, add `--upload-flags "--auth=<password>"`.

### C. Drop a `.bin` into a browser form: ElegantOTA

Open `http://<device-ip>/update` and drag a built `firmware.bin` file in. Useful if you want to try a one-off build without configuring PlatformIO to talk to the device.

### After any OTA path

The device reboots and reconnects to WiFi. The `[boot]` line in the serial log shows the new build tag so you can confirm the new image is the one running. The web page at `/` also shows the now-running build.

## Releasing a new firmware version

Just push to `main`. The workflow takes care of everything:

1. GitHub Actions checks out the code, installs PlatformIO, builds firmware-esp32s3 with `TINYCHAOS_BUILD_TAG = firmware-esp32s3-<UTC-timestamp>-<commit-sha7>` (this becomes the release tag).
2. A GitHub release is published with `tinychaos-esp32s3.bin`, `tinychaos-esp32s3.factory.bin`, and `SHA256SUMS.txt` attached.
3. Within a few minutes, every device's "latest" check will start reporting the new tag and the Update Now button on `/` lights up.

To force a build without changing firmware code, use the **Actions → Build & release firmware-esp32s3 → Run workflow** button on GitHub.

## Layout

```
firmware-esp32s3/
  platformio.ini                       PlatformIO config (pioarduino platform for Arduino-ESP32 v3.x)
  boards/esp32-s3-r8-opi.json          Waveshare board profile (copied from rsvpnano)
  tools/inject_build_tag.py            Stamps UTC build tag into TINYCHAOS_BUILD_TAG at build time
  lib/tinychaos_protocol/              The portable C protocol module (same files used by ../firmware/)
    entropy_config.h
    entropy_protocol.h
    entropy_protocol.c
  src/main.cpp                         ADC + WiFi + ArduinoOTA + ElegantOTA + USB CDC streaming
  src/wifi_config.h.template           Copy to wifi_config.h and fill in your network (gitignored)
  .gitignore                           Ignores .pio/, wifi_config.h, build outputs
  README.md                            This file
```

The `lib/tinychaos_protocol/` directory contains an unmodified copy of the three portable files from [../firmware/Core/Inc/entropy_config.h](../firmware/Core/Inc/entropy_config.h), [../firmware/Core/Inc/entropy_protocol.h](../firmware/Core/Inc/entropy_protocol.h), and [../firmware/Core/Src/entropy_protocol.c](../firmware/Core/Src/entropy_protocol.c). If the protocol ever changes, update all four copies (Python, C#, STM32, ESP32) together. The xUnit test in `analysis/tests/TinyChaos.Tests/PacketTests.cs::ParityWithPythonAndFirmware_FixedInput` is the cross-implementation parity check.

## Wiring options for first bring-up

You do not need the zener-and-amplifier analog front-end yet. To see live data in the GUI today, pick any of:

| Source                                          | What you'll see                                            |
|-------------------------------------------------|------------------------------------------------------------|
| Nothing wired to GPIO 1                         | Mostly noise floor + a bit of mains-coupled wander         |
| Finger on GPIO 1                                | Strong 50 Hz pickup; the trace breathes with your touch    |
| GPIO 1 to GND through 1 MΩ                      | Quiet, mostly ADC quantisation                             |
| GPIO 1 to 3.3 V through 1 MΩ                    | Quiet, pegged near 4095                                    |
| Potentiometer between 3.3 V and GND, tap to GPIO 1 | Whatever you dial in. Useful for sanity-checking the scale. |
| Microphone module with 3.3 V bias               | Live audio waveform                                        |

The 3.3 V absolute-maximum still applies. Do **not** feed anything above 3.3 V into the ADC pin without the clamp network from [../docs/adc-protection.md](../docs/adc-protection.md). The protection circuit in the project plan applies to this board the same way it applies to the NUCLEO.

## Integration with the rest of the project

- Wire format: identical to STM32 firmware. See [../docs/ENTROPY_CAPTURE_PIPELINE.md](../docs/ENTROPY_CAPTURE_PIPELINE.md) section 8.
- Host tools: same Python CLI, same C# CLI, same Avalonia GUI as for STM32 captures. No host changes.
- Sample directory: any `.bin` you record from this device works the same way the STM32 ones do in the GUI's Samples tab.
- Two-channel: when you add a second channel, pass two pins to `analogContinuous(...)` and the existing host `--channels 2` setting works unchanged.

For the bigger story of where this firmware sits in the project, see [../docs/FIRMWARE_INTEGRATION.md](../docs/FIRMWARE_INTEGRATION.md).
