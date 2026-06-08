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
    # No tools/, analysis/, firmware/, or bot tests under this empty REPO_DIR.
    monkeypatch.setattr(b, "REPO_DIR", tmp_path)
    monkeypatch.setattr(b, "BOT_DIR", tmp_path)
    ok, summary = run(b._run_tests())
    assert ok is True
    assert "no test suite" in summary or "skipped" in summary


def test_git_diff_stat_returns_string():
    out = run(b._git_diff_stat())
    assert isinstance(out, str)


# ---- _run_one_suite: the per-suite runner used to verify EVERY language ----

def test_run_one_suite_pass(tmp_path):
    state, summary = run(b._run_one_suite(["true"], tmp_path, 30))
    assert state == "pass"


def test_run_one_suite_fail_captures_output(tmp_path):
    # A command that prints then exits non-zero must be reported as a failure,
    # with its output preserved so the owner can see what broke.
    argv = ["python3", "-c", "print('boom detail'); import sys; sys.exit(1)"]
    state, summary = run(b._run_one_suite(argv, tmp_path, 30))
    assert state == "fail"
    assert "boom detail" in summary


def test_run_one_suite_missing_toolchain_skips(tmp_path):
    state, summary = run(b._run_one_suite(["tinychaos-no-such-binary-zzz"], tmp_path, 30))
    assert state == "skip"
    assert "PATH" in summary


def test_run_tests_aggregates_and_fails_if_any_fail(monkeypatch, tmp_path):
    # Point REPO_DIR/BOT_DIR at a fake tree with a tools/tests so one suite is
    # selected, and stub _run_one_suite so we control pass/fail aggregation.
    (tmp_path / "tools" / "tests").mkdir(parents=True)
    (tmp_path / "analysis").mkdir()
    (tmp_path / "analysis" / "TinyChaos.sln").write_text("")
    monkeypatch.setattr(b, "REPO_DIR", tmp_path)
    monkeypatch.setattr(b, "BOT_DIR", tmp_path / "nobot")

    async def fake_one(argv, cwd, timeout):
        return ("fail", "1 failed") if "dotnet" in argv[0] else ("pass", "ok")
    monkeypatch.setattr(b, "_run_one_suite", fake_one)

    ok, report = run(b._run_tests())
    assert ok is False                      # one suite failed -> overall fail
    assert "C# (.NET)" in report
    assert "Python tools" in report
