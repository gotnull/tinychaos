"""The slash-command menus: admin-only commands must not leak into the public
menu (the user explicitly required stats/reset be admin-only)."""


def _names(cmds):
    return [c.command for c in cmds]


def test_public_menu_is_ask_firmware_help(bot):
    assert _names(bot.PUBLIC_COMMANDS) == ["ask", "firmware", "help"]


def test_public_menu_excludes_admin_commands(bot):
    public = _names(bot.PUBLIC_COMMANDS)
    assert "stats" not in public
    assert "reset" not in public


def test_admin_menu_has_admin_commands(bot):
    admin = _names(bot.ADMIN_COMMANDS)
    assert "stats" in admin
    assert "reset" in admin
    assert "ask" in admin
    assert "help" in admin
