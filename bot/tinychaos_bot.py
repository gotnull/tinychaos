#!/usr/bin/env python3
"""
tinychaos Telegram Q&A bot.

A standalone bot (separate from StrozzBot) that sits in a Telegram group and
answers questions about THIS repository. Each question is handed to Claude Code
running headless (`claude -p`) in the repo directory, so it reads the actual
code/docs with grep/read and answers from the source - not from a static dump.

Safety: Claude is locked to READ-ONLY tools (Read/Grep/Glob) with all write and
shell tools disallowed, so it physically cannot edit, run commands, or commit.

Config (environment, e.g. via bot/.env):
    TELEGRAM_BOT_TOKEN       required - the new bot's token from @BotFather
    CLAUDE_CODE_OAUTH_TOKEN  required - from `claude setup-token` (so claude is
                             authed when run as a service; no browser login)
    TINYCHAOS_BOT_MODEL      optional - claude model (default claude-sonnet-4-6)
    TINYCHAOS_BOT_ALLOWED_CHATS  optional - comma-separated chat IDs to restrict
                             to (empty = answer anywhere the bot is added)
    TINYCHAOS_BOT_COOLDOWN   optional - per-user seconds between asks (default 15)

Commands:
    /ask <question>   ask about the repo (keeps short-term context per chat)
    /reset            clear this chat's conversation context
    /help, /start     usage
"""
import asyncio
import datetime
import json
import logging
import os
import re
import sqlite3
import time
from pathlib import Path

import telegramify_markdown
from dotenv import load_dotenv
from telegram import Update, BotCommand
from telegram.constants import ChatAction, ParseMode
from telegram.ext import (Application, CommandHandler, ContextTypes,
                          MessageHandler, filters)

# ---- Config --------------------------------------------------------------
BOT_DIR = Path(__file__).resolve().parent
REPO_DIR = BOT_DIR.parent                      # claude runs here = repo root
load_dotenv(BOT_DIR / ".env")

TOKEN = os.getenv("TELEGRAM_BOT_TOKEN", "").strip()
MODEL = os.getenv("TINYCHAOS_BOT_MODEL", "claude-sonnet-4-6").strip()
COOLDOWN = float(os.getenv("TINYCHAOS_BOT_COOLDOWN", "15"))
_allowed = os.getenv("TINYCHAOS_BOT_ALLOWED_CHATS", "").strip()
ALLOWED_CHATS = {int(x) for x in _allowed.split(",") if x.strip()} if _allowed else None
CLAUDE_TIMEOUT = 150  # seconds per question

SYSTEM_PROMPT = (
    "You are the tinychaos repo assistant answering questions in a Telegram "
    "group. tinychaos is a zener-diode avalanche-noise hardware RNG: STM32 "
    "NUCLEO-H753ZI + ESP32-S3 firmware, a Python toolchain, and a C# Avalonia "
    "GUI. Answer ONLY from what you read in this repository. Be concise and "
    "plain (a few short paragraphs max, no markdown tables) since this renders "
    "in a chat. You are read-only: never propose or make edits, never run shell "
    "commands. If something isn't in the repo, say so."
)

# Claude is allowed to READ the repo and nothing else. Anything able to write,
# edit, commit, or run arbitrary commands is explicitly denied.
ALLOWED_TOOLS = "Read,Grep,Glob"
DISALLOWED_TOOLS = "Bash,Edit,Write,MultiEdit,NotebookEdit,WebFetch,WebSearch"

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("tinychaos-bot")

# Serialize Claude runs (each is heavy) and track per-user cooldown + per-chat
# session id (for short conversational context via `claude --resume`).
_claude_lock = asyncio.Lock()
_last_ask: dict[int, float] = {}


# ---- Persistent memory (SQLite, gitignored) -----------------------------
# Survives restarts: the per-chat Claude session id (so conversational context
# carries across bot/service restarts) plus a log of every Q&A (which chat, who
# asked, the answer, and the cost) so there's a record of activity - the
# tinychaos-specific equivalent of StrozzBot's bot_memory.db.
DB_PATH = BOT_DIR / "bot_memory.db"


def _db() -> sqlite3.Connection:
    conn = sqlite3.connect(DB_PATH, timeout=10)
    conn.execute("PRAGMA journal_mode=WAL")
    return conn


def init_db() -> None:
    with _db() as c:
        c.execute("CREATE TABLE IF NOT EXISTS sessions("
                  "chat_id INTEGER PRIMARY KEY, session_id TEXT, updated_at TEXT)")
        c.execute("CREATE TABLE IF NOT EXISTS interactions("
                  "id INTEGER PRIMARY KEY AUTOINCREMENT, chat_id INTEGER, chat_title TEXT,"
                  " user_id INTEGER, username TEXT, question TEXT, answer TEXT,"
                  " cost_usd REAL, created_at TEXT)")


def _now() -> str:
    return datetime.datetime.utcnow().isoformat(timespec="seconds")


def db_get_session(chat_id: int):
    with _db() as c:
        row = c.execute("SELECT session_id FROM sessions WHERE chat_id=?", (chat_id,)).fetchone()
    return row[0] if row else None


def db_set_session(chat_id: int, session_id: str) -> None:
    with _db() as c:
        c.execute("INSERT INTO sessions(chat_id, session_id, updated_at) VALUES(?,?,?) "
                  "ON CONFLICT(chat_id) DO UPDATE SET session_id=excluded.session_id, "
                  "updated_at=excluded.updated_at", (chat_id, session_id, _now()))


def db_clear_session(chat_id: int) -> None:
    with _db() as c:
        c.execute("DELETE FROM sessions WHERE chat_id=?", (chat_id,))


def db_log(chat_id, title, user_id, username, question, answer, cost) -> None:
    with _db() as c:
        c.execute("INSERT INTO interactions(chat_id, chat_title, user_id, username, question, "
                  "answer, cost_usd, created_at) VALUES(?,?,?,?,?,?,?,?)",
                  (chat_id, title, user_id, username, question, (answer or "")[:4000], cost, _now()))


def db_stats():
    with _db() as c:
        total, cost = c.execute(
            "SELECT COUNT(*), COALESCE(SUM(cost_usd),0) FROM interactions").fetchone()
        rows = c.execute("SELECT COALESCE(chat_title,'?'), COUNT(*), COALESCE(SUM(cost_usd),0) "
                         "FROM interactions GROUP BY chat_id ORDER BY 2 DESC LIMIT 8").fetchall()
    return total, cost, rows


def _split(text: str, n: int = 3900):
    """Telegram caps messages at 4096 chars; yield <=n-char chunks."""
    text = text.strip() or "(no answer)"
    for i in range(0, len(text), n):
        yield text[i:i + n]


async def _reply(msg, text: str) -> None:
    """Send an answer to Telegram. Renders markdown - code blocks (```), inline
    `code`, **bold**, etc. - via MarkdownV2 (which syntax-highlights fenced code
    by language) when it fits and is valid. Falls back to plain-text chunks if
    the converted message would exceed Telegram's 4096-char limit or Telegram
    rejects the formatting, so a reply always gets through."""
    text = (text or "").strip() or "(no answer)"
    try:
        md = telegramify_markdown.markdownify(text)
    except Exception:
        md = None
    if md is not None and len(md) <= 4096:
        try:
            await msg.reply_text(md, parse_mode=ParseMode.MARKDOWN_V2)
            return
        except Exception as e:
            log.warning("MarkdownV2 send failed (%s); falling back to plain text", e)
    for chunk in _split(text):
        await msg.reply_text(chunk)


async def _ask_claude(question: str, session_id: str | None) -> tuple[str, str | None, float | None]:
    """Run `claude -p` read-only in the repo; return (answer, new_session_id, cost_usd)."""
    cmd = [
        "claude", "-p", question,
        "--output-format", "json",
        "--model", MODEL,
        "--allowedTools", ALLOWED_TOOLS,
        "--disallowedTools", DISALLOWED_TOOLS,
        "--append-system-prompt", SYSTEM_PROMPT,
    ]
    if session_id:
        cmd += ["--resume", session_id]

    proc = await asyncio.create_subprocess_exec(
        *cmd, cwd=str(REPO_DIR),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
    )
    try:
        out, err = await asyncio.wait_for(proc.communicate(), timeout=CLAUDE_TIMEOUT)
    except asyncio.TimeoutError:
        proc.kill()
        return ("Timed out reading the repo - try a narrower question.", session_id, None)

    if proc.returncode != 0:
        log.error("claude exited %s: %s", proc.returncode, err.decode(errors="replace")[:500])
        return ("Sorry - I couldn't reach Claude (auth/CLI error). Check the bot logs.", session_id, None)

    try:
        data = json.loads(out.decode(errors="replace"))
        return (data.get("result", "(empty answer)"),
                data.get("session_id") or session_id,
                data.get("total_cost_usd"))
    except json.JSONDecodeError:
        # Fall back to raw text if the output wasn't JSON for some reason.
        return (out.decode(errors="replace").strip() or "(empty answer)", session_id, None)


def _allowed_here(update: Update) -> bool:
    return ALLOWED_CHATS is None or (update.effective_chat and update.effective_chat.id in ALLOWED_CHATS)


async def _run_ask(update: Update, context: ContextTypes.DEFAULT_TYPE, question: str) -> None:
    """Shared question path used by both /ask and @mentions/replies: cooldown,
    one-run-at-a-time, persisted per-chat context, log to memory, reply."""
    msg = update.effective_message
    chat = update.effective_chat
    chat_id = chat.id
    user = update.effective_user
    user_id = user.id if user else 0
    username = (user.username or user.full_name) if user else "?"
    title = getattr(chat, "title", None)
    log.info("ASK chat id=%s title=%r user=%r q=%r", chat_id, title, username, question[:80])

    # Per-user cooldown so the group can't spam expensive runs.
    now = time.monotonic()
    wait = COOLDOWN - (now - _last_ask.get(user_id, 0))
    if wait > 0:
        await msg.reply_text(f"Hang on - {wait:.0f}s before your next question.")
        return
    _last_ask[user_id] = now

    await context.bot.send_chat_action(chat_id=chat_id, action=ChatAction.TYPING)
    async with _claude_lock:  # one Claude run at a time
        answer, session, cost = await _ask_claude(question, db_get_session(chat_id))
    if session:
        db_set_session(chat_id, session)            # persist context across restarts
    db_log(chat_id, title, user_id, username, question, answer, cost)
    await _reply(msg, answer)


async def cmd_ask(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed_here(update):
        return
    question = " ".join(context.args).strip() if context.args else ""
    if not question:
        await update.effective_message.reply_text(
            "Ask me about the tinychaos repo, e.g. `/ask how does the USB CDC path work?` "
            "(or just @mention me with a question)")
        return
    await _run_ask(update, context, question)


async def on_mention(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Answer when the bot is @mentioned or replied to, treating the message as
    the question - so `@TinyChaosBot how does X work?` behaves like /ask."""
    if not _allowed_here(update):
        return
    msg = update.effective_message
    text = (msg.text or msg.caption or "") if msg else ""
    if not text:
        return
    me = context.bot.username  # set by PTB after startup, e.g. "TinyChaosBot"
    mention = f"@{me}" if me else None
    mentioned = bool(mention) and mention.lower() in text.lower()
    is_reply_to_bot = bool(
        msg.reply_to_message and msg.reply_to_message.from_user
        and msg.reply_to_message.from_user.id == context.bot.id)
    if not (mentioned or is_reply_to_bot):
        return  # not addressed to us

    question = re.sub(re.escape(mention), "", text, flags=re.IGNORECASE).strip() if mention else text.strip()
    if not question:
        await msg.reply_text("Ask me something about the tinychaos repo - e.g. how does the ADC DMA work?")
        return
    await _run_ask(update, context, question)


async def cmd_reset(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed_here(update):
        return
    db_clear_session(update.effective_chat.id)
    await update.effective_message.reply_text("Context cleared - next question starts fresh.")


async def cmd_stats(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Show how many questions have been answered and the running Claude cost."""
    if not _allowed_here(update):
        return
    total, cost, rows = db_stats()
    lines = [f"questions answered: {total}", f"total cost: ${cost:.2f}"]
    for title, n, c in rows:
        lines.append(f"  {title}: {n} (${c:.2f})")
    await update.effective_message.reply_text("\n".join(lines))


async def cmd_chatid(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Report this chat's id - paste it into TINYCHAOS_BOT_ALLOWED_CHATS to lock
    the bot to this group. Intentionally NOT gated by _allowed_here so you can
    use it before the allowlist is set."""
    chat = update.effective_chat
    log.info("CHATID chat id=%s type=%s title=%r", chat.id, chat.type, getattr(chat, "title", None))
    await update.effective_message.reply_text(
        f"chat id: {chat.id}\ntype: {chat.type}\n"
        f"(group ids are negative; put it in TINYCHAOS_BOT_ALLOWED_CHATS)"
    )


async def cmd_help(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    await update.effective_message.reply_text(
        "tinychaos repo assistant - I read this codebase live and answer questions.\n"
        "  @mention me with a question, e.g. \"@bot how does the USB CDC path work?\"\n"
        "  /ask <question>  - same thing as a command\n"
        "  /reset           - clear this chat's conversation context\n"
        "  /stats           - questions answered + running cost\n"
        "I'm read-only: I answer from the repository, I never edit it."
    )


# The slash-command menu shown in Telegram (the list when you type "/"). Set
# programmatically via the Bot API on every startup, so it always matches the
# code - no need to touch BotFather when commands change. Edit this list and
# restart; the new menu pushes automatically.
BOT_COMMANDS = [
    BotCommand("ask", "Ask about the tinychaos repo (I read it live)"),
    BotCommand("stats", "Questions answered + running cost"),
    BotCommand("reset", "Clear this chat's conversation context"),
    BotCommand("help", "What I can do"),
]


async def _post_init(app: Application) -> None:
    await app.bot.set_my_commands(BOT_COMMANDS)
    log.info("registered %d slash commands with Telegram", len(BOT_COMMANDS))


def main() -> None:
    if not TOKEN:
        raise SystemExit("TELEGRAM_BOT_TOKEN not set (see bot/.env.example)")
    if not os.getenv("CLAUDE_CODE_OAUTH_TOKEN") and not os.getenv("ANTHROPIC_API_KEY"):
        log.warning("Neither CLAUDE_CODE_OAUTH_TOKEN nor ANTHROPIC_API_KEY set - "
                    "`claude -p` may fail to authenticate when run as a service.")
    init_db()

    app = Application.builder().token(TOKEN).post_init(_post_init).build()
    app.add_handler(CommandHandler("ask", cmd_ask))
    app.add_handler(CommandHandler("reset", cmd_reset))
    app.add_handler(CommandHandler("stats", cmd_stats))
    app.add_handler(CommandHandler("chatid", cmd_chatid))
    app.add_handler(CommandHandler(["help", "start"], cmd_help))
    # Natural @mentions / replies to the bot (with group privacy on, these are
    # the only non-command messages the bot receives anyway). Commands are
    # excluded so /ask isn't double-handled.
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, on_mention))
    log.info("tinychaos bot up; repo=%s model=%s", REPO_DIR, MODEL)
    app.run_polling(allowed_updates=Update.ALL_TYPES)


if __name__ == "__main__":
    main()
