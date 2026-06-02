"""Shared fixtures for the tinychaos bot tests.

The bot module is imported once and its module-level DB_PATH is redirected to a
throwaway file per-test, so the tests never touch the real bot_memory.db. The
HAL refusal cycler's global state is reset between tests so cycling assertions
are deterministic.
"""
import sys
import types
from pathlib import Path

import pytest

BOT_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(BOT_DIR))

import tinychaos_bot as b  # noqa: E402


@pytest.fixture
def bot():
    """The imported bot module."""
    return b


@pytest.fixture
def tmp_db(tmp_path, monkeypatch):
    """A fresh, isolated SQLite database for one test."""
    monkeypatch.setattr(b, "DB_PATH", tmp_path / "test.db")
    b.init_db()
    return b


@pytest.fixture(autouse=True)
def _reset_hal():
    """Reset the HAL cycler globals so cycling tests are independent."""
    b._hal_pool = []
    b._hal_last = None
    yield


@pytest.fixture
def make_update():
    """Factory for a minimal telegram Update stand-in used in gating tests."""
    def _make(user_id, chat_id, chat_type="group"):
        return types.SimpleNamespace(
            effective_user=types.SimpleNamespace(id=user_id),
            effective_chat=types.SimpleNamespace(id=chat_id, type=chat_type),
        )
    return _make
