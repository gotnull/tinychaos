"""Pure text helpers: edit-marker stripping, message splitting, relative time."""
import datetime

import pytest


# ---- _strip_edit_marker --------------------------------------------------

def test_marker_absent_returns_false(bot):
    clean, avail = bot._strip_edit_marker("Just an explanation, no change.")
    assert avail is False
    assert clean == "Just an explanation, no change."


def test_marker_on_own_line_stripped(bot):
    raw = "Here is the fix.\n" + bot.EDIT_MARKER
    clean, avail = bot._strip_edit_marker(raw)
    assert avail is True
    assert clean == "Here is the fix."
    assert bot.EDIT_MARKER not in clean


def test_marker_inline_with_trailing_space(bot):
    raw = "Change foo to bar. " + bot.EDIT_MARKER + "   "
    clean, avail = bot._strip_edit_marker(raw)
    assert avail is True
    assert clean == "Change foo to bar."


def test_marker_only(bot):
    clean, avail = bot._strip_edit_marker(bot.EDIT_MARKER)
    assert avail is True
    assert clean == ""


def test_marker_with_blank_lines_before(bot):
    raw = "Apply this.\n\n\n" + bot.EDIT_MARKER
    clean, avail = bot._strip_edit_marker(raw)
    assert avail is True
    assert clean == "Apply this."


# ---- _split --------------------------------------------------------------

def test_split_short_single_chunk(bot):
    chunks = list(bot._split("hello"))
    assert chunks == ["hello"]


def test_split_empty_yields_placeholder(bot):
    chunks = list(bot._split("   "))
    assert chunks == ["(no answer)"]


def test_split_long_respects_limit(bot):
    text = "x" * 9000
    chunks = list(bot._split(text, n=3900))
    assert all(len(c) <= 3900 for c in chunks)
    assert "".join(chunks) == text
    assert len(chunks) == 3


def test_split_breaks_on_line_boundaries(bot):
    text = "\n".join(f"line {i} with some words" for i in range(500))
    chunks = list(bot._split(text, n=300))
    assert all(len(c) <= 300 for c in chunks)
    # no chunk starts or ends mid-line (every line is intact somewhere)
    assert all(not c.startswith(" ") for c in chunks)


def test_long_markdown_keeps_markdownv2_not_plain(bot):
    """A long answer (>4096) must still render bold/code as MarkdownV2 chunks,
    not get dumped as raw plain text (the literal-stars bug)."""
    import asyncio
    text = "\n".join(f"**Section {i}** with `code{i}` and detail." for i in range(200))
    sent = []

    async def fake_send(t, parse_mode, rm):
        sent.append((t, parse_mode))
        return object()

    asyncio.run(bot._send_chunked_markdown(fake_send, text))
    assert len(sent) >= 2                                   # it was chunked
    assert all(pm is not None for _, pm in sent)            # every chunk is MarkdownV2
    assert all("**" not in t for t, _ in sent)              # bold converted, no literal **


# ---- _fmt_ago ------------------------------------------------------------

def test_fmt_ago_none(bot):
    assert bot._fmt_ago(None) == ""


def test_fmt_ago_just_now(bot):
    now = datetime.datetime.utcnow().isoformat(timespec="seconds")
    assert bot._fmt_ago(now) == "just now"


def test_fmt_ago_minutes(bot):
    t = (datetime.datetime.utcnow() - datetime.timedelta(minutes=10)).isoformat(timespec="seconds")
    assert bot._fmt_ago(t) == "10 min ago"


def test_fmt_ago_hours(bot):
    t = (datetime.datetime.utcnow() - datetime.timedelta(hours=3)).isoformat(timespec="seconds")
    assert bot._fmt_ago(t) == "3 h ago"


def test_fmt_ago_days(bot):
    t = (datetime.datetime.utcnow() - datetime.timedelta(days=2)).isoformat(timespec="seconds")
    assert bot._fmt_ago(t) == "2 d ago"


def test_fmt_ago_garbage_returns_input(bot):
    assert bot._fmt_ago("not-a-date") == "not-a-date"
