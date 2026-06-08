"""Persistence layer: sessions, interaction logging, and the /stats snapshot."""
import datetime


# ---- sessions ------------------------------------------------------------

def test_session_roundtrip(tmp_db):
    assert tmp_db.db_get_session(123) is None
    tmp_db.db_set_session(123, "sess-abc")
    assert tmp_db.db_get_session(123) == "sess-abc"


def test_session_update_overwrites(tmp_db):
    tmp_db.db_set_session(1, "a")
    tmp_db.db_set_session(1, "b")
    assert tmp_db.db_get_session(1) == "b"


def test_session_clear(tmp_db):
    tmp_db.db_set_session(1, "a")
    tmp_db.db_clear_session(1)
    assert tmp_db.db_get_session(1) is None


def test_session_stale_is_abandoned(tmp_db):
    # A session whose last update is older than SESSION_MAX_AGE_SEC must not be
    # resumed (it would reload a huge transcript and time out).
    with tmp_db._db() as c:
        c.execute("INSERT INTO sessions(chat_id, session_id, updated_at) VALUES(?,?,?)",
                  (9, "old-sess", "2000-01-01T00:00:00"))
    assert tmp_db.db_get_session(9) is None


def test_session_fresh_is_returned(tmp_db):
    tmp_db.db_set_session(9, "fresh-sess")   # updated_at = now
    assert tmp_db.db_get_session(9) == "fresh-sess"


# ---- interactions + stats ------------------------------------------------

def test_stats_empty(tmp_db):
    s = tmp_db.db_stats()
    assert s["total"] == 0
    assert s["cost"] == 0
    assert s["chats"] == []
    assert s["users"] == []
    assert s["last"] is None


def test_stats_counts_and_cost(tmp_db):
    tmp_db.db_log(-100, "Group A", 1, "alice", "q1", "a1", 0.05)
    tmp_db.db_log(-100, "Group A", 2, "bob", "q2", "a2", 0.10)
    tmp_db.db_log(-200, None, 1, "alice", "q3", "a3", 0.02)  # DM (no title)
    s = tmp_db.db_stats()
    assert s["total"] == 3
    assert round(s["cost"], 2) == 0.17
    assert s["today_n"] == 3
    assert s["week_n"] == 3


def test_stats_top_chats_and_users(tmp_db):
    for _ in range(3):
        tmp_db.db_log(-100, "Busy", 1, "alice", "q", "a", 0.01)
    tmp_db.db_log(-200, "Quiet", 2, "bob", "q", "a", 0.01)
    s = tmp_db.db_stats()
    # Busy chat first (3 vs 1); alice top asker (3 vs 1).
    assert s["chats"][0][0] == "Busy"
    assert s["chats"][0][1] == 3
    assert s["users"][0][0] == "alice"
    assert s["users"][0][1] == 3


def test_stats_dm_labelled(tmp_db):
    tmp_db.db_log(-200, None, 1, "alice", "q", "a", 0.01)
    s = tmp_db.db_stats()
    assert s["chats"][0][0] == "DM"  # COALESCE(chat_title,'DM')


def test_stats_last_interaction(tmp_db):
    tmp_db.db_log(-100, "Group", 7, "carol", "latest?", "ans", 0.01)
    s = tmp_db.db_stats()
    when, who, where = s["last"]
    assert who == "carol"
    assert where == "Group"


def test_stats_excludes_old_from_today(tmp_db):
    # Insert a row dated long ago directly, plus one fresh row.
    with tmp_db._db() as c:
        c.execute("INSERT INTO interactions(chat_id, chat_title, user_id, username,"
                  " question, answer, cost_usd, created_at) VALUES(?,?,?,?,?,?,?,?)",
                  (-100, "Old", 1, "alice", "q", "a", 0.01, "2000-01-01T00:00:00"))
    tmp_db.db_log(-100, "New", 1, "alice", "q", "a", 0.01)
    s = tmp_db.db_stats()
    assert s["total"] == 2
    assert s["today_n"] == 1   # the 2000 row is excluded from today
    assert s["week_n"] == 1


def test_stats_session_count(tmp_db):
    tmp_db.db_set_session(1, "s1")
    tmp_db.db_set_session(2, "s2")
    assert tmp_db.db_stats()["sessions"] == 2
