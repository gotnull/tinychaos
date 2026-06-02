"""The owner-approved edit request store: add, fetch, status transitions."""


def test_pending_unknown_token_is_none(tmp_db):
    assert tmp_db.db_get_pending("nope") is None


def test_pending_add_defaults_to_offered(tmp_db):
    tmp_db.db_add_pending("tok1", -100, 555, "Group", 1, "@alice", "add a flag", "sess-1")
    p = tmp_db.db_get_pending("tok1")
    assert p["token"] == "tok1"
    assert p["orig_chat_id"] == -100
    assert p["orig_msg_id"] == 555
    assert p["chat_title"] == "Group"
    assert p["requester_id"] == 1
    assert p["requester_name"] == "@alice"
    assert p["question"] == "add a flag"
    assert p["session_id"] == "sess-1"
    assert p["status"] == "offered"


def test_pending_status_transition(tmp_db):
    tmp_db.db_add_pending("tok2", -1, 0, "G", 1, "@a", "q", None)
    tmp_db.db_set_pending("tok2", status="requested", requester_id=9, requester_name="@bob")
    p = tmp_db.db_get_pending("tok2")
    assert p["status"] == "requested"
    assert p["requester_id"] == 9
    assert p["requester_name"] == "@bob"


def test_pending_status_only_update_keeps_requester(tmp_db):
    tmp_db.db_add_pending("tok3", -1, 0, "G", 5, "@orig", "q", None)
    tmp_db.db_set_pending("tok3", status="applied")
    p = tmp_db.db_get_pending("tok3")
    assert p["status"] == "applied"
    assert p["requester_id"] == 5          # untouched
    assert p["requester_name"] == "@orig"


def test_pending_set_with_no_fields_is_noop(tmp_db):
    tmp_db.db_add_pending("tok4", -1, 0, "G", 1, "@a", "q", None)
    tmp_db.db_set_pending("tok4")          # nothing to set
    assert tmp_db.db_get_pending("tok4")["status"] == "offered"


def test_pending_replace_same_token(tmp_db):
    tmp_db.db_add_pending("tok5", -1, 0, "G", 1, "@a", "first", None)
    tmp_db.db_add_pending("tok5", -1, 0, "G", 1, "@a", "second", None)  # INSERT OR REPLACE
    assert tmp_db.db_get_pending("tok5")["question"] == "second"
