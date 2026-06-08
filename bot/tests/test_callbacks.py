"""Callback-query robustness: a stale/expired query must not crash the handler.

This guards the bug where 'Apply this change' was acknowledged AFTER the slow
owner-DM step, so the callback id expired (~15s) and the BadRequest bubbled up
as an unhandled exception, silently dropping the request.
"""
import asyncio

import tinychaos_bot as b


class _Query:
    """Minimal callback-query stand-in. answer() can be made to raise to
    simulate Telegram's 'Query is too old' BadRequest."""
    def __init__(self, raises=False):
        self.raises = raises
        self.answered = False

    async def answer(self, text=None, show_alert=False):
        self.answered = True
        if self.raises:
            raise RuntimeError("Query is too old and response timeout expired")


def test_safe_answer_normal():
    q = _Query()
    asyncio.run(b._safe_answer(q, "ok"))
    assert q.answered is True


def test_safe_answer_swallows_stale_query():
    q = _Query(raises=True)
    # Must NOT raise even though query.answer() blows up.
    asyncio.run(b._safe_answer(q, "late", alert=True))
    assert q.answered is True


def test_safe_answer_no_text():
    q = _Query()
    asyncio.run(b._safe_answer(q))
    assert q.answered is True
