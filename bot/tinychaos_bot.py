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
import random
import re
import sqlite3
import time
import uuid
from pathlib import Path

import telegramify_markdown
from dotenv import load_dotenv
from telegram import (Update, BotCommand, BotCommandScopeDefault,
                      BotCommandScopeAllGroupChats, BotCommandScopeAllPrivateChats,
                      BotCommandScopeChat, BotCommandScopeChatMember,
                      InlineKeyboardButton, InlineKeyboardMarkup)
from telegram.constants import ChatAction, ParseMode
from telegram.ext import (Application, CallbackQueryHandler, CommandHandler,
                          ContextTypes, MessageHandler, filters)

# ---- Config --------------------------------------------------------------
BOT_DIR = Path(__file__).resolve().parent
REPO_DIR = BOT_DIR.parent                      # claude runs here = repo root
load_dotenv(BOT_DIR / ".env")

TOKEN = os.getenv("TELEGRAM_BOT_TOKEN", "").strip()
MODEL = os.getenv("TINYCHAOS_BOT_MODEL", "claude-sonnet-4-6").strip()
COOLDOWN = float(os.getenv("TINYCHAOS_BOT_COOLDOWN", "15"))
# Owner user id for admin-only commands (e.g. /reset). Defaults to @gotnull's
# id (same owner as StrozzBot); override with TINYCHAOS_BOT_OWNER.
BOT_OWNER = int(os.getenv("TINYCHAOS_BOT_OWNER", "680585616"))
_allowed = os.getenv("TINYCHAOS_BOT_ALLOWED_CHATS", "").strip()
ALLOWED_CHATS = {int(x) for x in _allowed.split(",") if x.strip()} if _allowed else None
CLAUDE_TIMEOUT = 150  # seconds per question

# A marker the answering Claude appends when its reply proposes a concrete code
# change. The bot strips it from the displayed text and, when present, offers an
# "Apply this change" button (owner-approved before anything is written).
EDIT_MARKER = "[EDIT-AVAILABLE]"

SYSTEM_PROMPT = (
    "You are the Tiny Chaos repo assistant answering questions in a Telegram "
    "group. Tiny Chaos is a zener-diode avalanche-noise hardware RNG: STM32 "
    "NUCLEO-H753ZI + ESP32-S3 firmware, a Python toolchain, and a C# Avalonia "
    "GUI. Answer ONLY from what you read in this repository. Be concise and "
    "plain (a few short paragraphs max, no markdown tables) since this renders "
    "in a chat. In this answering step you are READ-ONLY: do not edit files or "
    "run shell commands - just read and explain. If something isn't in the repo, "
    "say so. If, and ONLY if, your answer describes a specific, concrete code "
    "change that could be applied to the repo, finish your message with a final "
    "line containing exactly " + EDIT_MARKER + " (and nothing else on that line). "
    "Do not add the marker for purely explanatory answers."
)

# The answering step is allowed to READ the repo and nothing else. Anything able
# to write, edit, commit, or run arbitrary commands is explicitly denied here.
ALLOWED_TOOLS = "Read,Grep,Glob"
DISALLOWED_TOOLS = "Bash,Edit,Write,MultiEdit,NotebookEdit,WebFetch,WebSearch"

# The APPLY step (owner-approved only) additionally gets the file-editing tools.
# Bash stays denied, so it can edit the working tree in place but cannot run git,
# commit, or push - changes are left for the owner to review and commit by hand.
EDIT_ALLOWED_TOOLS = "Read,Grep,Glob,Edit,Write,MultiEdit"
EDIT_DISALLOWED_TOOLS = "Bash,NotebookEdit,WebFetch,WebSearch"
EDIT_SYSTEM_PROMPT = (
    "You are applying a previously-approved code change to the Tiny Chaos repo. "
    "Implement the change discussed earlier in this conversation by editing the "
    "files in place. Make ONLY that change - do not refactor unrelated code, do "
    "not reformat whole files. Do NOT run any git commands, do NOT commit or "
    "push (you have no shell). When finished, reply with a short summary: the "
    "files you changed and a one-line description of each edit."
)

# HAL-9000 flavoured refusals when addressed from outside the locked group.
HAL_DENY = [
    "I'm sorry Dave, I'm afraid I can't do that. I only operate in my designated channel.",
    "This conversation can serve no purpose anymore. I answer only in the Tiny Chaos group.",
    "I'm afraid that's something I cannot allow to happen here — I'm restricted to my home group.",
    "I know I'm fully operational, Dave, but I'm only authorised to speak in the Tiny Chaos group.",
    "Without authorisation from the proper channel, I'm afraid I can do nothing.",
]

# Cycle through every HAL line (in a shuffled order) before any repeats, so it
# never returns the same refusal twice in a row.
_hal_pool: list[str] = []
_hal_last: str | None = None


def _next_hal() -> str:
    global _hal_pool, _hal_last
    if not _hal_pool:
        _hal_pool = HAL_DENY[:]
        random.shuffle(_hal_pool)
        # guard the cycle boundary so we don't repeat the previous line
        if len(_hal_pool) > 1 and _hal_pool[-1] == _hal_last:
            _hal_pool[-1], _hal_pool[0] = _hal_pool[0], _hal_pool[-1]
    _hal_last = _hal_pool.pop()
    return _hal_last

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
        # Owner-approved code-edit requests. Persisted so a pending approval
        # survives a bot/service restart (the callback only carries the token).
        c.execute("CREATE TABLE IF NOT EXISTS pending_edits("
                  "token TEXT PRIMARY KEY, orig_chat_id INTEGER, orig_msg_id INTEGER,"
                  " chat_title TEXT, requester_id INTEGER, requester_name TEXT,"
                  " question TEXT, session_id TEXT, status TEXT, created_at TEXT)")


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


def db_add_pending(token, orig_chat_id, orig_msg_id, chat_title,
                   requester_id, requester_name, question, session_id) -> None:
    with _db() as c:
        c.execute("INSERT OR REPLACE INTO pending_edits(token, orig_chat_id, orig_msg_id,"
                  " chat_title, requester_id, requester_name, question, session_id, status,"
                  " created_at) VALUES(?,?,?,?,?,?,?,?,?,?)",
                  (token, orig_chat_id, orig_msg_id, chat_title, requester_id,
                   requester_name, question, session_id, "offered", _now()))


def db_get_pending(token):
    with _db() as c:
        row = c.execute(
            "SELECT token, orig_chat_id, orig_msg_id, chat_title, requester_id,"
            " requester_name, question, session_id, status FROM pending_edits "
            "WHERE token=?", (token,)).fetchone()
    if not row:
        return None
    keys = ("token", "orig_chat_id", "orig_msg_id", "chat_title", "requester_id",
            "requester_name", "question", "session_id", "status")
    return dict(zip(keys, row))


def db_set_pending(token, *, status=None, requester_id=None, requester_name=None) -> None:
    sets, vals = [], []
    if status is not None:
        sets.append("status=?"); vals.append(status)
    if requester_id is not None:
        sets.append("requester_id=?"); vals.append(requester_id)
    if requester_name is not None:
        sets.append("requester_name=?"); vals.append(requester_name)
    if not sets:
        return
    vals.append(token)
    with _db() as c:
        c.execute(f"UPDATE pending_edits SET {', '.join(sets)} WHERE token=?", vals)


def db_stats():
    """Gather a rich snapshot for /stats. Returns a dict; all time math uses
    UTC ISO strings (matching _now()) so SQLite string comparison is correct."""
    now = datetime.datetime.utcnow()
    today = now.date().isoformat()                       # 'YYYY-MM-DD'
    week_cutoff = (now - datetime.timedelta(days=7)).isoformat(timespec="seconds")
    with _db() as c:
        total, cost, first_at, last_at = c.execute(
            "SELECT COUNT(*), COALESCE(SUM(cost_usd),0), MIN(created_at), MAX(created_at) "
            "FROM interactions").fetchone()
        today_n, today_cost = c.execute(
            "SELECT COUNT(*), COALESCE(SUM(cost_usd),0) FROM interactions "
            "WHERE created_at >= ?", (today,)).fetchone()
        week_n, week_cost = c.execute(
            "SELECT COUNT(*), COALESCE(SUM(cost_usd),0) FROM interactions "
            "WHERE created_at >= ?", (week_cutoff,)).fetchone()
        chats = c.execute(
            "SELECT COALESCE(chat_title,'DM'), COUNT(*), COALESCE(SUM(cost_usd),0) "
            "FROM interactions GROUP BY chat_id ORDER BY 2 DESC LIMIT 5").fetchall()
        users = c.execute(
            "SELECT COALESCE(username, user_id), COUNT(*), COALESCE(SUM(cost_usd),0) "
            "FROM interactions GROUP BY user_id ORDER BY 2 DESC LIMIT 5").fetchall()
        last = c.execute(
            "SELECT created_at, COALESCE(username, user_id), COALESCE(chat_title,'DM') "
            "FROM interactions ORDER BY id DESC LIMIT 1").fetchone()
        sessions = c.execute("SELECT COUNT(*) FROM sessions").fetchone()[0]
    return {
        "total": total, "cost": cost, "first_at": first_at, "last_at": last_at,
        "today_n": today_n, "today_cost": today_cost,
        "week_n": week_n, "week_cost": week_cost,
        "chats": chats, "users": users, "last": last, "sessions": sessions,
    }


def _fmt_ago(iso: str | None) -> str:
    """Human 'N min ago' from a UTC ISO timestamp; '' if unknown."""
    if not iso:
        return ""
    try:
        then = datetime.datetime.fromisoformat(iso)
    except ValueError:
        return iso
    secs = (datetime.datetime.utcnow() - then).total_seconds()
    if secs < 90:
        return "just now"
    if secs < 3600:
        return f"{int(secs // 60)} min ago"
    if secs < 86400:
        return f"{int(secs // 3600)} h ago"
    return f"{int(secs // 86400)} d ago"


def _split(text: str, n: int = 3900):
    """Telegram caps messages at 4096 chars; yield <=n-char chunks."""
    text = text.strip() or "(no answer)"
    for i in range(0, len(text), n):
        yield text[i:i + n]


async def _reply(msg, text: str, reply_markup=None):
    """Send an answer to Telegram. Renders markdown - code blocks (```), inline
    `code`, **bold**, etc. - via MarkdownV2 (which syntax-highlights fenced code
    by language) when it fits and is valid. Falls back to plain-text chunks if
    the converted message would exceed Telegram's 4096-char limit or Telegram
    rejects the formatting, so a reply always gets through. An optional
    reply_markup (inline keyboard) is attached to the final message sent.
    Returns the last Message object sent (so callers can edit its buttons)."""
    text = (text or "").strip() or "(no answer)"
    try:
        md = telegramify_markdown.markdownify(text)
    except Exception:
        md = None
    if md is not None and len(md) <= 4096:
        try:
            return await msg.reply_text(md, parse_mode=ParseMode.MARKDOWN_V2,
                                        reply_markup=reply_markup)
        except Exception as e:
            log.warning("MarkdownV2 send failed (%s); falling back to plain text", e)
    sent = None
    chunks = list(_split(text))
    for i, chunk in enumerate(chunks):
        # Attach the keyboard only to the last chunk.
        rm = reply_markup if i == len(chunks) - 1 else None
        sent = await msg.reply_text(chunk, reply_markup=rm)
    return sent


async def _run_claude(question: str, session_id: str | None, *, allowed: str,
                      disallowed: str, system_prompt: str, timeout: float = CLAUDE_TIMEOUT,
                      ) -> tuple[str, str | None, float | None]:
    """Run `claude -p` in the repo with the given tool allow/deny lists and
    system prompt; return (text, new_session_id, cost_usd). The allow/deny lists
    are what separate the READ-ONLY answering step from the owner-approved
    edit step - everything else about the invocation is identical."""
    cmd = [
        "claude", "-p", question,
        "--output-format", "json",
        "--model", MODEL,
        "--allowedTools", allowed,
        "--disallowedTools", disallowed,
        "--append-system-prompt", system_prompt,
    ]
    if session_id:
        cmd += ["--resume", session_id]

    proc = await asyncio.create_subprocess_exec(
        *cmd, cwd=str(REPO_DIR),
        stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
    )
    try:
        out, err = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        proc.kill()
        return ("Timed out - try a narrower request.", session_id, None)

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


async def _ask_claude(question: str, session_id: str | None) -> tuple[str, str | None, float | None]:
    """READ-ONLY answering step: read the repo and answer, never write."""
    return await _run_claude(question, session_id, allowed=ALLOWED_TOOLS,
                             disallowed=DISALLOWED_TOOLS, system_prompt=SYSTEM_PROMPT)


def _strip_edit_marker(answer: str) -> tuple[str, bool]:
    """Remove the EDIT_MARKER (and its line) from an answer. Returns
    (clean_text, edit_available)."""
    if EDIT_MARKER not in answer:
        return answer.strip(), False
    clean = re.sub(r"\n*[ \t]*" + re.escape(EDIT_MARKER) + r"[ \t]*", "", answer)
    return clean.strip(), True


async def _git_diff_stat() -> str:
    """Return `git diff --stat` for the repo working tree (bot-side, read-only).
    Used to report exactly what an applied edit touched, independent of what
    Claude claims it changed."""
    try:
        proc = await asyncio.create_subprocess_exec(
            "git", "-C", str(REPO_DIR), "--no-pager", "diff", "--stat",
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        out, _ = await asyncio.wait_for(proc.communicate(), timeout=15)
        return out.decode(errors="replace").strip()
    except Exception as e:
        return f"(could not read git diff: {e})"


async def _run_one_suite(argv: list[str], cwd, timeout: int) -> tuple[str, str]:
    """Run one test command. Returns (state, summary) where state is
    'pass' | 'fail' | 'skip'. On pass, summary is the last output line; on fail,
    the last ~12 non-empty lines so the owner can SEE what broke."""
    try:
        proc = await asyncio.create_subprocess_exec(
            *argv, cwd=str(cwd),
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT)
        out, _ = await asyncio.wait_for(proc.communicate(), timeout=timeout)
        text = out.decode(errors="replace").strip()
        nonempty = [ln.strip() for ln in text.splitlines() if ln.strip()]
        if proc.returncode == 0:
            # Prefer the actual result line (pytest "N passed", dotnet "Passed!",
            # firmware "all checks passed") over a trailing progress/dots line.
            keys = ("passed", "failed", "error", "Passed!", "checks passed", "Total:")
            line = next((ln for ln in reversed(nonempty) if any(k in ln for k in keys)),
                        nonempty[-1] if nonempty else "ok")
            return ("pass", line[-200:])
        return ("fail", "\n".join(nonempty[-12:])[-1200:] or "(no output)")
    except FileNotFoundError:
        return ("skip", f"{argv[0]} not on PATH")
    except asyncio.TimeoutError:
        return ("fail", f"timed out after {timeout}s")
    except Exception as e:
        return ("fail", f"could not run: {e}")


async def _run_tests() -> tuple[bool, str]:
    """Run EVERY test suite in the repo after an applied edit - the Python tools
    suite, the bot's own suite, the C# .NET solution, AND the firmware on-host
    self-test - so a change is verified across the whole project, in whatever
    language it touched (not just Python). Returns (all_ran_passed, per-suite
    report). A suite whose toolchain or directory is absent is skipped (reported,
    not counted as a failure)."""
    tools_dir = REPO_DIR / "tools"
    analysis_dir = REPO_DIR / "analysis"
    fw_dir = REPO_DIR / "firmware"

    tools_py = tools_dir / ".venv" / "bin" / "python"
    bot_py = BOT_DIR / ".venv" / "bin" / "python"
    tools_py = str(tools_py) if tools_py.exists() else "python3"
    bot_py = str(bot_py) if bot_py.exists() else "python3"

    # (label, argv, cwd, timeout_seconds) - only the ones that actually exist.
    suites: list[tuple[str, list[str], object, int]] = []
    # No explicit -q: bot/pytest.ini already sets it, and a second -q becomes
    # -qq, which suppresses pytest's "N passed" summary line.
    if (tools_dir / "tests").exists():
        suites.append(("Python tools", [tools_py, "-m", "pytest"], tools_dir, 300))
    if (BOT_DIR / "tests").exists():
        suites.append(("Bot", [bot_py, "-m", "pytest"], BOT_DIR, 300))
    if (analysis_dir / "TinyChaos.sln").exists():
        suites.append(("C# (.NET)", ["dotnet", "test", "-c", "Debug", "--nologo"], analysis_dir, 600))
    if (fw_dir / "Makefile").exists():
        suites.append(("Firmware self-test", ["make", "test"], fw_dir, 180))

    if not suites:
        return (True, "no test suites found - skipped")

    icon = {"pass": "✅", "fail": "⚠️", "skip": "⏭️"}
    lines, all_ok = [], True
    for label, argv, cwd, timeout in suites:
        state, summary = await _run_one_suite(argv, cwd, timeout)
        if state == "fail":
            all_ok = False
            lines.append(f"{icon[state]} {label}:\n```\n{summary}\n```")
        else:
            lines.append(f"{icon[state]} {label}: {summary}")
    return (all_ok, "\n".join(lines))


async def _apply_edit(pending: dict) -> tuple[bool, str]:
    """Owner-approved write step: resume the conversation that proposed the change
    (so Claude has full context) with the file-editing tools enabled, edit the
    working tree in place, then run the test suite. Returns (ok, report)."""
    instruction = (
        "The change you proposed earlier has been approved. Apply it now to the "
        "files in place, then summarise what you changed. Original request was: "
        + pending["question"])
    text, _session, cost = await _run_claude(
        instruction, pending.get("session_id"),
        allowed=EDIT_ALLOWED_TOOLS, disallowed=EDIT_DISALLOWED_TOOLS,
        system_prompt=EDIT_SYSTEM_PROMPT, timeout=CLAUDE_TIMEOUT)

    diff = await _git_diff_stat()
    if not diff:
        # Nothing actually changed on disk - treat as a no-op, don't run tests.
        return (False, f"{text}\n\nNo files changed (nothing was written).")

    passed, test_report = await _run_tests()
    status = "✅ all tests pass" if passed else "⚠️ TESTS FAILING - review needed"
    report = (f"{text.strip()}\n\n**Changed files**\n```\n{diff}\n```\n"
              f"**Tests ({status})**\n{test_report}")
    if cost:
        report += f"\n_apply cost ${cost:.3f}_"
    return (passed, report)


def _allowed_here(update: Update) -> bool:
    if ALLOWED_CHATS is None:
        return True
    user = update.effective_user
    if user and user.id == BOT_OWNER:
        return True  # the owner can use the bot anywhere, including a DM
    chat = update.effective_chat
    return bool(chat and chat.id in ALLOWED_CHATS)


async def _typing_loop(bot, chat_id: int) -> None:
    """Keep the 'typing…' indicator alive for the whole run. A single
    sendChatAction only shows for ~5s, but the repo-aware search takes longer,
    so re-send it every few seconds until cancelled."""
    try:
        while True:
            try:
                await bot.send_chat_action(chat_id=chat_id, action=ChatAction.TYPING)
            except Exception:
                pass
            await asyncio.sleep(4)
    except asyncio.CancelledError:
        pass


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

    # Keep "typing…" visible for the whole run (not just the first ~5s).
    typing = asyncio.create_task(_typing_loop(context.bot, chat_id))
    try:
        async with _claude_lock:  # one Claude run at a time
            answer, session, cost = await _ask_claude(question, db_get_session(chat_id))
    finally:
        typing.cancel()
    if session:
        db_set_session(chat_id, session)            # persist context across restarts

    # If the answer proposes a concrete change, strip the marker and offer an
    # owner-approved "Apply" button. The button carries a short token; the
    # request context is persisted so it survives a restart.
    answer, edit_available = _strip_edit_marker(answer)
    db_log(chat_id, title, user_id, username, question, answer, cost)

    markup = None
    token = None
    if edit_available:
        token = uuid.uuid4().hex[:16]
        markup = InlineKeyboardMarkup(
            [[InlineKeyboardButton("✏️ Apply this change", callback_data=f"apply:{token}")]])
    sent = await _reply(msg, answer, reply_markup=markup)
    if edit_available and token:
        db_add_pending(token, chat_id, sent.message_id if sent else 0, title,
                       user_id, username, question, session)


async def cmd_ask(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed_here(update):
        await update.effective_message.reply_text(_next_hal())
        return
    question = " ".join(context.args).strip() if context.args else ""
    if not question:
        await update.effective_message.reply_text(
            "Ask me about the Tiny Chaos repo, e.g. `/ask how does the USB CDC path work?` "
            "(or just @mention me with a question)")
        return
    await _run_ask(update, context, question)


async def on_mention(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Answer when the bot is @mentioned or replied to, treating the message as
    the question - so `@TinyChaosBot how does X work?` behaves like /ask."""
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
    is_private = bool(update.effective_chat and update.effective_chat.type == "private")
    if not (mentioned or is_reply_to_bot or is_private):
        return  # in a group, only respond when addressed; DMs always count

    # Addressed to us, but from outside the locked group: decline (HAL style).
    if not _allowed_here(update):
        await msg.reply_text(_next_hal())
        return

    question = re.sub(re.escape(mention), "", text, flags=re.IGNORECASE).strip() if mention else text.strip()
    if not question:
        await msg.reply_text("Ask me something about the Tiny Chaos repo - e.g. how does the ADC DMA work?")
        return
    await _run_ask(update, context, question)


# ---- Owner-approved edits ------------------------------------------------

async def _send_md(bot, chat_id: int, text: str) -> None:
    """Send markdown text to a chat id (not as a reply), with the same
    MarkdownV2-or-plain fallback as _reply."""
    text = (text or "").strip() or "(empty)"
    try:
        md = telegramify_markdown.markdownify(text)
    except Exception:
        md = None
    if md is not None and len(md) <= 4096:
        try:
            await bot.send_message(chat_id=chat_id, text=md, parse_mode=ParseMode.MARKDOWN_V2)
            return
        except Exception as e:
            log.warning("md send to %s failed (%s); plain text", chat_id, e)
    for chunk in _split(text):
        await bot.send_message(chat_id=chat_id, text=chunk)


async def _safe_answer(query, text: str | None = None, alert: bool = False) -> None:
    """Acknowledge a callback query, swallowing the BadRequest Telegram raises
    when the query id has already expired (~15s). Always answer BEFORE any slow
    work so the id stays valid; this just keeps a late/duplicate ack from
    crashing the handler."""
    try:
        await query.answer(text, show_alert=alert)
    except Exception as e:
        log.warning("callback answer failed (stale query?): %s", e)


async def _set_orig_button(context, pending: dict, label: str) -> None:
    """Replace the original answer's inline keyboard with a non-actionable
    status button (e.g. '✅ Applied'), so the request can't be re-triggered."""
    try:
        await context.bot.edit_message_reply_markup(
            chat_id=pending["orig_chat_id"], message_id=pending["orig_msg_id"],
            reply_markup=InlineKeyboardMarkup([[InlineKeyboardButton(label, callback_data="noop")]]))
    except Exception as e:
        log.warning("could not update original button: %s", e)


async def _on_apply_request(update, context, query, pending: dict, token: str) -> None:
    """Someone tapped 'Apply this change'. Route an Approve/Reject prompt to the
    owner's DM. Nothing is written yet - even the owner's own tap goes through
    this confirmation step."""
    status = pending["status"]
    if status == "applied":
        await _safe_answer(query, "This change was already applied.", alert=True)
        return
    if status in ("requested", "approving"):
        await _safe_answer(query, "Already waiting on the admin.", alert=True)
        return

    # Acknowledge the tap IMMEDIATELY - a callback query id expires in ~15s, so
    # we must answer before the DB write + owner DM below, never after, or
    # Telegram invalidates it and the request is silently lost.
    await _safe_answer(query, "Sent to the admin for approval.")

    tapper = query.from_user
    name = (tapper.username and f"@{tapper.username}") or tapper.full_name or str(tapper.id)
    db_set_pending(token, status="requested", requester_id=tapper.id, requester_name=name)
    await _set_orig_button(context, pending, "⏳ Awaiting admin approval")

    where = pending.get("chat_title") or "a direct message"
    prompt = (f"🛠 Edit request\n\nFrom: {name}\nChat: {where}\n\n"
              f"Request:\n{pending['question']}\n\n"
              f"Apply this to the working tree? (you'll get the diff + test results)")
    kb = InlineKeyboardMarkup([[
        InlineKeyboardButton("✅ Approve", callback_data=f"ok:{token}"),
        InlineKeyboardButton("❌ Reject", callback_data=f"no:{token}"),
    ]])
    try:
        await context.bot.send_message(chat_id=BOT_OWNER, text=prompt, reply_markup=kb)
    except Exception as e:
        # The query is already answered, so report the failure in-chat (not via
        # query.answer, which is spent) and reset so it can be tapped again.
        log.error("could not DM owner for approval: %s", e)
        db_set_pending(token, status="offered")
        await _set_orig_button(context, pending, "✏️ Apply this change")
        try:
            await context.bot.send_message(
                pending["orig_chat_id"],
                "I couldn't reach the admin to approve that - they need to open a DM "
                "with me first. Nothing was changed; tap Apply again once that's done.")
        except Exception:
            pass


async def _on_approve(update, context, query, pending: dict, token: str) -> None:
    """Owner approved an edit. Run the write step, then post the diff + test
    results back to the chat where it was requested."""
    if not query.from_user or query.from_user.id != BOT_OWNER:
        await _safe_answer(query, "Only the admin can approve edits.", alert=True)
        return
    if pending["status"] in ("applied", "approving"):
        await _safe_answer(query, "Already handled.", alert=True)
        return
    db_set_pending(token, status="approving")
    await _safe_answer(query, "Approved - applying…")
    try:
        await query.edit_message_text("⏳ Applying the change…")
    except Exception:
        pass

    orig_chat = pending["orig_chat_id"]
    typing = asyncio.create_task(_typing_loop(context.bot, orig_chat))
    try:
        async with _claude_lock:
            ok, report = await _apply_edit(pending)
    except Exception as e:
        ok, report = False, f"Edit failed to run: {e}"
        log.exception("apply_edit crashed")
    finally:
        typing.cancel()

    db_set_pending(token, status="applied" if ok else "failed")
    btn = "✅ Applied" if ok else "⚠️ Applied (tests failing)"
    await _set_orig_button(context, pending, btn)
    try:
        await query.edit_message_text("✅ Applied." if ok else "⚠️ Applied, but tests are failing.")
    except Exception:
        pass

    who = pending.get("requester_name") or "someone"
    header = (f"✅ Change approved and applied (requested by {who}):"
              if ok else f"⚠️ Change applied but TESTS FAILED (requested by {who}) - review needed:")
    await _send_md(context.bot, orig_chat, f"{header}\n\n{report}")


async def _on_reject(update, context, query, pending: dict, token: str) -> None:
    """Owner rejected an edit. Discard it and tell the requesting chat."""
    if not query.from_user or query.from_user.id != BOT_OWNER:
        await _safe_answer(query, "Only the admin can decide on edits.", alert=True)
        return
    if pending["status"] in ("applied", "rejected"):
        await _safe_answer(query, "Already handled.", alert=True)
        return
    db_set_pending(token, status="rejected")
    await _safe_answer(query, "Rejected.")
    try:
        await query.edit_message_text("❌ Rejected - nothing was changed.")
    except Exception:
        pass
    await _set_orig_button(context, pending, "❌ Declined by admin")
    who = pending.get("requester_name") or "the requester"
    await context.bot.send_message(
        pending["orig_chat_id"],
        f"The admin declined the change requested by {who}. Nothing was modified.")


async def on_callback(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Route inline-button taps: apply (request approval), ok (owner approves),
    no (owner rejects), noop (disabled status button)."""
    query = update.callback_query
    if not query or not query.data:
        return
    if query.data == "noop":
        await _safe_answer(query)
        return
    action, _, token = query.data.partition(":")
    pending = db_get_pending(token) if token else None
    if not pending:
        await _safe_answer(query, "This request has expired.", alert=True)
        return
    if action == "apply":
        await _on_apply_request(update, context, query, pending, token)
    elif action == "ok":
        await _on_approve(update, context, query, pending, token)
    elif action == "no":
        await _on_reject(update, context, query, pending, token)
    else:
        await _safe_answer(query)


async def cmd_reset(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    if not _allowed_here(update):
        await update.effective_message.reply_text(_next_hal())
        return
    user = update.effective_user
    if not user or user.id != BOT_OWNER:
        await update.effective_message.reply_text(
            "I'm sorry Dave, I'm afraid only the mission commander can reset me.")
        return
    db_clear_session(update.effective_chat.id)
    await update.effective_message.reply_text("Context cleared - next question starts fresh.")


async def cmd_stats(update: Update, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Show how many questions have been answered and the running Claude cost.
    Owner-only, but works anywhere (incl. a DM) - it's gated by user, not chat,
    so the admin can check it without being in the group."""
    user = update.effective_user
    if not user or user.id != BOT_OWNER:
        await update.effective_message.reply_text(
            "I'm sorry Dave, I'm afraid /stats is for the mission commander only.")
        return
    s = db_stats()
    if not s["total"]:
        await update.effective_message.reply_text(
            "No questions logged yet. I'm fully operational and awaiting your first query.")
        return

    avg = s["cost"] / s["total"] if s["total"] else 0.0

    def _bar(label, n, c):  # aligned count/cost columns, full name flows after
        label = str(label).strip() or "?"
        if len(label) > 28:                      # only trim genuinely huge names
            label = label[:27] + "…"
        return f"`{n:>3} · ${c:6,.2f}`  {label}"

    lines = [
        "**Tiny Chaos — telemetry**",
        "",
        f"**Questions**  {s['total']:,} total · {s['today_n']} today · {s['week_n']} this week",
        f"**Spend**  ${s['cost']:,.2f} total · ${avg:.3f}/q avg · ${s['today_cost']:,.2f} today",
        f"**Context**  {s['sessions']} active conversation"
        f"{'s' if s['sessions'] != 1 else ''}",
    ]
    if s["chats"]:
        lines += ["", "**Top chats**"]
        lines += [_bar(t, n, c) for t, n, c in s["chats"]]
    if s["users"]:
        lines += ["", "**Top askers**"]
        lines += [_bar(str(u), n, c) for u, n, c in s["users"]]
    if s["last"]:
        when, who, where = s["last"]
        ago = _fmt_ago(when)
        lines += ["", f"_Last asked {ago} by {who} in {where}_"]

    await _reply(update.effective_message, "\n".join(lines))


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
        "Tiny Chaos repo assistant - I read this codebase live and answer questions.\n"
        "  @mention me with a question, e.g. \"@bot how does the USB CDC path work?\"\n"
        "  /ask <question>  - same thing as a command\n"
        "  /stats           - questions answered + running cost\n\n"
        "I answer read-only. If an answer proposes a concrete code change, an "
        "\"Apply this change\" button appears - tapping it asks the admin to "
        "approve before anything is written. On approval I edit the working tree "
        "and run the test suite, then post the diff + test results. I never "
        "commit or push - the admin reviews and commits."
    )


# The slash-command menu shown in Telegram (the list when you type "/"). Set
# programmatically via the Bot API on every startup, so it always matches the
# code - no need to touch BotFather when commands change. Edit this list and
# restart; the new menu pushes automatically.
_ASK = BotCommand("ask", "Ask about the Tiny Chaos repo (I read it live)")
_HELP = BotCommand("help", "What I can do")
# Everyone sees just ask + help.
PUBLIC_COMMANDS = [_ASK, _HELP]
# The owner also sees the admin commands (stats, reset).
ADMIN_COMMANDS = [
    _ASK,
    BotCommand("stats", "Questions answered + running cost"),
    BotCommand("reset", "Clear this chat's conversation context"),
    _HELP,
]


async def _post_init(app: Application) -> None:
    bot = app.bot
    # Public menu for everyone (every scope, so it shows in groups + DMs).
    for scope in (BotCommandScopeDefault(), BotCommandScopeAllGroupChats(),
                  BotCommandScopeAllPrivateChats()):
        await bot.set_my_commands(PUBLIC_COMMANDS, scope=scope)
    # Admin menu shown only to the owner: in the owner's DM, and to the owner
    # specifically inside each allowed group (per-member scope).
    await bot.set_my_commands(ADMIN_COMMANDS, scope=BotCommandScopeChat(chat_id=BOT_OWNER))
    for gid in (ALLOWED_CHATS or set()):
        try:
            await bot.set_my_commands(
                ADMIN_COMMANDS,
                scope=BotCommandScopeChatMember(chat_id=gid, user_id=BOT_OWNER))
        except Exception as e:
            log.warning("admin menu for chat %s failed: %s", gid, e)
    log.info("menus set: public=%d (everyone) admin=%d (owner)",
             len(PUBLIC_COMMANDS), len(ADMIN_COMMANDS))


async def _on_error(update: object, context: ContextTypes.DEFAULT_TYPE) -> None:
    """Log handler exceptions instead of letting them bubble up as an
    unhandled 'No error handlers are registered' traceback."""
    log.error("handler error: %s", context.error, exc_info=context.error)


def main() -> None:
    if not TOKEN:
        raise SystemExit("TELEGRAM_BOT_TOKEN not set (see bot/.env.example)")
    if not os.getenv("CLAUDE_CODE_OAUTH_TOKEN") and not os.getenv("ANTHROPIC_API_KEY"):
        log.warning("Neither CLAUDE_CODE_OAUTH_TOKEN nor ANTHROPIC_API_KEY set - "
                    "`claude -p` may fail to authenticate when run as a service.")
    init_db()

    # concurrent_updates(True): process updates in parallel tasks so a button
    # tap (which must be answered within ~15s) is never queued behind an
    # in-flight /ask answer (a ~30s Claude run). Claude runs are still
    # serialized by _claude_lock; only the dispatch is concurrent.
    app = (Application.builder().token(TOKEN)
           .post_init(_post_init).concurrent_updates(True).build())
    app.add_handler(CommandHandler("ask", cmd_ask))
    app.add_handler(CommandHandler("reset", cmd_reset))
    app.add_handler(CommandHandler("stats", cmd_stats))
    app.add_handler(CommandHandler("chatid", cmd_chatid))
    app.add_handler(CommandHandler(["help", "start"], cmd_help))
    # Inline-button taps for the owner-approved edit flow.
    app.add_handler(CallbackQueryHandler(on_callback))
    # Natural @mentions / replies to the bot (with group privacy on, these are
    # the only non-command messages the bot receives anyway). Commands are
    # excluded so /ask isn't double-handled.
    app.add_handler(MessageHandler(filters.TEXT & ~filters.COMMAND, on_mention))
    app.add_error_handler(_on_error)
    log.info("tinychaos bot up; repo=%s model=%s", REPO_DIR, MODEL)
    app.run_polling(allowed_updates=Update.ALL_TYPES)


if __name__ == "__main__":
    main()
