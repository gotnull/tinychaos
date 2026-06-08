"""send_to_group.render must highlight code AND refuse the bold-around-code
break (the bug that made `/firmware` render as literal `*/firmware*`)."""
import importlib

send = importlib.import_module("send_to_group")


def test_render_plain_code_and_bold_ok():
    out = send.render("Run `./flash.sh`. **It just works.**")
    assert "`./flash.sh`" in out          # code span preserved
    assert "`*" not in out and "*`" not in out


def test_render_rejects_bold_around_bare_code():
    import pytest
    with pytest.raises(ValueError):
        send.render("Type **`/firmware`** now")


def test_render_allows_bold_phrase_containing_code():
    # Bold around a phrase that merely CONTAINS code is fine.
    out = send.render("**1. Ignore `gemini-cli` entirely.**")
    assert "`gemini-cli`" in out
    assert "`*" not in out and "*`" not in out


def test_render_keeps_fenced_block():
    out = send.render("```bash\ncd firmware\n./flash.sh\n```")
    assert "```" in out
