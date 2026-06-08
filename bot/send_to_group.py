#!/usr/bin/env python3
r"""Send or edit a message in the Tiny Chaos Telegram group, rendered with the
SAME MarkdownV2 pipeline the bot's /ask replies use - so code highlights
correctly instead of showing literal markdown.

Usage (from bot/, venv active or via .venv/bin/python):
    echo "**Heads up** - run \`./flash.sh\`" | python send_to_group.py
    python send_to_group.py --file note.md
    python send_to_group.py --edit 174 --file fixed.md

Formatting rule this enforces: keep `code` spans and **bold** SEPARATE - never
wrap a bare code span in bold. telegramify turns `**`+backtick+code+backtick+`**`
into a code span full of literal asterisks (`` `*code*` ``), which renders as
monospace *code*. This script refuses to send if that happened.
"""
import argparse
import json
import os
import sys
import urllib.error
import urllib.request
import urllib.parse
from pathlib import Path

import telegramify_markdown
from dotenv import load_dotenv

BOT_DIR = Path(__file__).resolve().parent
load_dotenv(BOT_DIR / ".env")

TOKEN = os.getenv("TELEGRAM_BOT_TOKEN", "").strip()
CHAT = int(os.getenv("TINYCHAOS_GROUP_CHAT", "-5013051641"))


def render(text: str) -> str:
    """Markdown -> MarkdownV2, refusing the bold-around-bare-code break."""
    md = telegramify_markdown.markdownify(text)
    if "`*" in md or "*`" in md:
        raise ValueError(
            "bold wrapped a code span (`**`code`**`) - it would render as "
            "literal asterisks. Keep `code` and **bold** separate.")
    return md


def _call(method: str, payload: dict) -> dict:
    data = urllib.parse.urlencode(payload).encode()
    url = f"https://api.telegram.org/bot{TOKEN}/{method}"
    with urllib.request.urlopen(urllib.request.Request(url, data=data)) as r:
        return json.load(r)


def main() -> None:
    ap = argparse.ArgumentParser(description="Send/edit a Tiny Chaos group message.")
    ap.add_argument("--edit", type=int, metavar="MSG_ID",
                    help="edit this message id instead of sending a new one")
    ap.add_argument("--file", help="read text from this file (default: stdin)")
    args = ap.parse_args()

    if not TOKEN:
        sys.exit("TELEGRAM_BOT_TOKEN not set (see bot/.env)")
    text = Path(args.file).read_text() if args.file else sys.stdin.read()
    md = render(text)

    payload = {"chat_id": CHAT, "text": md, "parse_mode": "MarkdownV2",
               "disable_web_page_preview": "true"}
    method = "sendMessage"
    if args.edit:
        method = "editMessageText"
        payload["message_id"] = args.edit
    try:
        r = _call(method, payload)
        print(f"{method} ok={r.get('ok')} msg_id={r.get('result', {}).get('message_id')}")
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} FAILED: {e.read().decode()}")


if __name__ == "__main__":
    main()
