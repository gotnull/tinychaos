# tinychaos Telegram bot

A standalone Telegram bot that answers questions about **this repository**. Each
question is handed to **Claude Code running headless** (`claude -p`) inside the
repo, so it reads the actual code/docs/firmware with grep+read and answers from
the source. It is **read-only** — Claude is locked to Read/Grep/Glob with all
write/shell tools denied, so it can never edit, run commands, or commit.

It is fully separate from StrozzBot (its own bot token, its own process).

## Setup

1. **Create a new bot** with [@BotFather](https://t.me/BotFather) → `/newbot`,
   copy the token. (Don't reuse StrozzBot's token.)

2. **Auth Claude headless** — once, on this machine:
   ```
   claude setup-token
   ```
   Copy the printed token (a long-lived OAuth token).

3. **Configure**:
   ```
   cd bot
   cp .env.example .env
   # edit .env: paste TELEGRAM_BOT_TOKEN and CLAUDE_CODE_OAUTH_TOKEN
   ```

4. **Install + run**:
   ```
   python3 -m venv .venv
   source .venv/bin/activate
   pip install -r requirements.txt
   python tinychaos_bot.py
   ```

5. **Add the bot to your group.** Then ask:
   ```
   /ask how does the USB CDC transport work?
   /ask what are the two ADC channels for?
   /reset            (clears the chat's conversation context)
   ```
   In a group, use `/ask` (or `/ask@yourbotname` if there are multiple bots).
   With the default BotFather group-privacy setting the bot still receives its
   own `/commands`; if you later want it to also answer plain @mentions, run
   `/setprivacy → Disable` in BotFather.

## Run it as a service (optional, macOS launchd)

Edit `com.tinychaos.bot.plist` (paths are placeholders), then:
```
cp com.tinychaos.bot.plist ~/Library/LaunchAgents/
launchctl load ~/Library/LaunchAgents/com.tinychaos.bot.plist
```
The `.env` provides both tokens (it's loaded into the environment, which the
`claude` subprocess inherits), so the plist itself needs no secrets.

## How it's locked down

`tinychaos_bot.py` invokes:
```
claude -p "<question>" --output-format json --model <model>
       --allowedTools "Read,Grep,Glob"
       --disallowedTools "Bash,Edit,Write,MultiEdit,NotebookEdit,WebFetch,WebSearch"
       --append-system-prompt "<repo-scoped, read-only, concise>"
       [--resume <session>]   # short per-chat conversational context
```
- **Read-only:** no Bash/Edit/Write means no commits, no shell, no file changes.
- **Cost control:** one Claude run at a time + a per-user cooldown (`TINYCHAOS_BOT_COOLDOWN`).
- **Scope:** optional `TINYCHAOS_BOT_ALLOWED_CHATS` restricts it to specific groups.

## Cost / latency note

Each `/ask` is a full Claude agent run (~10–40 s, and it spends tokens). The
cooldown + single-run lock keep a busy group from running up a bill; raise the
cooldown or set `TINYCHAOS_BOT_ALLOWED_CHATS` if needed.
