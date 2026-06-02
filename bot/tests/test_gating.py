"""Access control: who may use the bot where (_allowed_here)."""


def test_allowed_when_no_allowlist(bot, monkeypatch, make_update):
    monkeypatch.setattr(bot, "ALLOWED_CHATS", None)
    assert bot._allowed_here(make_update(user_id=999, chat_id=-1)) is True


def test_owner_allowed_anywhere(bot, monkeypatch, make_update):
    monkeypatch.setattr(bot, "ALLOWED_CHATS", {-5013051641})
    monkeypatch.setattr(bot, "BOT_OWNER", 680585616)
    # Owner in some random chat (e.g. a DM) is still allowed.
    assert bot._allowed_here(make_update(user_id=680585616, chat_id=42, chat_type="private")) is True


def test_nonowner_allowed_in_listed_chat(bot, monkeypatch, make_update):
    monkeypatch.setattr(bot, "ALLOWED_CHATS", {-5013051641})
    monkeypatch.setattr(bot, "BOT_OWNER", 680585616)
    assert bot._allowed_here(make_update(user_id=111, chat_id=-5013051641)) is True


def test_nonowner_denied_elsewhere(bot, monkeypatch, make_update):
    monkeypatch.setattr(bot, "ALLOWED_CHATS", {-5013051641})
    monkeypatch.setattr(bot, "BOT_OWNER", 680585616)
    assert bot._allowed_here(make_update(user_id=111, chat_id=-999)) is False
