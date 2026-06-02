"""The write step: _apply_edit, _run_tests, _git_diff_stat. The Claude CLI and
git are stubbed so these run offline and never touch real files (except the
read-only git diff smoke test, which only reads the repo)."""
import asyncio

import tinychaos_bot as b


def run(coro):
    return asyncio.run(coro)


def test_apply_noop_when_nothing_changed(monkeypatch):
    async def fake_run_claude(*a, **k):
        return ("I considered it but made no change.", "sess", 0.01)

    async def fake_diff():
        return ""  # nothing written

    monkeypatch.setattr(b, "_run_claude", fake_run_claude)
    monkeypatch.setattr(b, "_git_diff_stat", fake_diff)
    ok, report = run(b._apply_edit({"question": "do x", "session_id": "sess"}))
    assert ok is False
    assert "No files changed" in report


def test_apply_success_runs_tests_and_reports(monkeypatch):
    async def fake_run_claude(*a, **k):
        return ("Edited main.c to add the flag.", "s", 0.02)

    async def fake_diff():
        return " firmware/Core/Src/main.c | 4 ++--"

    async def fake_tests():
        return (True, "3 passed in 0.10s")

    monkeypatch.setattr(b, "_run_claude", fake_run_claude)
    monkeypatch.setattr(b, "_git_diff_stat", fake_diff)
    monkeypatch.setattr(b, "_run_tests", fake_tests)
    ok, report = run(b._apply_edit({"question": "q", "session_id": None}))
    assert ok is True
    assert "main.c" in report
    assert "tests pass" in report
    assert "3 passed" in report


def test_apply_reports_test_failure(monkeypatch):
    async def fake_run_claude(*a, **k):
        return ("Edited.", "s", None)

    async def fake_diff():
        return " x.py | 1 +"

    async def fake_tests():
        return (False, "1 failed, 2 passed")

    monkeypatch.setattr(b, "_run_claude", fake_run_claude)
    monkeypatch.setattr(b, "_git_diff_stat", fake_diff)
    monkeypatch.setattr(b, "_run_tests", fake_tests)
    ok, report = run(b._apply_edit({"question": "q", "session_id": None}))
    assert ok is False
    assert "TESTS FAILING" in report
    assert "1 failed" in report


def test_apply_passes_edit_tools_not_readonly(monkeypatch):
    captured = {}

    async def fake_run_claude(question, session_id, **k):
        captured.update(k)
        captured["question"] = question
        return ("done", "s", 0.0)

    async def fake_diff():
        return " a | 1 +"

    async def fake_tests():
        return (True, "ok")

    monkeypatch.setattr(b, "_run_claude", fake_run_claude)
    monkeypatch.setattr(b, "_git_diff_stat", fake_diff)
    monkeypatch.setattr(b, "_run_tests", fake_tests)
    run(b._apply_edit({"question": "add feature Z", "session_id": "sess"}))
    # The apply step must use the write-enabled tool list, not the read-only one.
    assert captured["allowed"] == b.EDIT_ALLOWED_TOOLS
    assert "Edit" in captured["allowed"] and "Write" in captured["allowed"]
    # Bash must stay denied so it can't commit/push.
    assert "Bash" in captured["disallowed"]
    assert "add feature Z" in captured["question"]  # original request carried through


def test_run_tests_skips_when_no_suite(monkeypatch, tmp_path):
    monkeypatch.setattr(b, "REPO_DIR", tmp_path)  # empty dir, no tools/
    ok, summary = run(b._run_tests())
    assert ok is True
    assert "no test suite" in summary or "skipped" in summary


def test_git_diff_stat_returns_string():
    out = run(b._git_diff_stat())
    assert isinstance(out, str)
