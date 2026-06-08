"""/firmware command: the pure release-picking logic + menu registration."""
import tinychaos_bot as b


def test_pick_firmware_release_finds_h753():
    releases = [
        {"tag_name": "something-else-1", "html_url": "u0", "assets": []},
        {"tag_name": "firmware-h753-260608-x", "html_url": "u1",
         "assets": [{"name": "tinychaos-h753-uart.bin", "browser_download_url": "dl1"},
                    {"name": "tinychaos-h753-usb.bin", "browser_download_url": "dl2"}]},
    ]
    html_url, tag, assets = b._pick_firmware_release(releases)
    assert html_url == "u1"
    assert tag == "firmware-h753-260608-x"
    assert ("tinychaos-h753-uart.bin", "dl1") in assets
    assert len(assets) == 2


def test_pick_firmware_release_skips_non_firmware():
    releases = [{"tag_name": "v1.0", "html_url": "u", "assets": []}]
    assert b._pick_firmware_release(releases) is None


def test_pick_firmware_release_empty():
    assert b._pick_firmware_release([]) is None


def test_pick_firmware_release_ignores_unnamed_assets():
    releases = [{"tag_name": "firmware-h753-x", "html_url": "u",
                 "assets": [{"browser_download_url": "dl"}, {"name": "ok.bin", "browser_download_url": "d2"}]}]
    _, _, assets = b._pick_firmware_release(releases)
    assert assets == [("ok.bin", "d2")]


def test_firmware_in_public_menu():
    names = [c.command for c in b.PUBLIC_COMMANDS]
    assert "firmware" in names
