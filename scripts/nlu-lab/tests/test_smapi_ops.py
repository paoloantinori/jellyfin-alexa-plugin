"""Pure-parse tests for smapi_ops: no network, no ask CLI (fakes only).

The pinned payload samples are the shapes the live ask CLI emits: the
manifest/status samples tolerate the outer body wrapper because the CLI's
--full-response mode wraps, and smapi_client's simulate parsing unwraps the
same way.
"""
import subprocess
import time

import _paths
_paths.bootstrap()

import smapi_ops
from battery import BatteryRow
from runs import RowStatus
from smapi_client import SmapiError
from smapi_ops import parse_build_status, parse_skill_name, run_battery_rows


def _rows(*utterances):
    return [BatteryRow(utterance=u, source="fixture", expected_intent=None)
            for u in utterances]


def _ok_response(utterance):
    return {
        "selectedIntent": {
            "name": "PlaySongIntent",
            "slots": {"song": {"name": "song", "value": utterance}},
        },
        "consideredIntents": [{"name": "PlaySongIntent"}, {"name": "PlayAlbumIntent"}],
    }


# --- parse_skill_name ---

def test_parse_skill_name_pins_manifest_sample():
    payload = {"manifest": {"publishingInformation":
                            {"locales": {"en-US": {"name": "jellyfin nlu lab"}}}}}
    assert parse_skill_name(payload) == "jellyfin nlu lab"


def test_parse_skill_name_falls_back_to_first_locale():
    payload = {"manifest": {"publishingInformation":
                            {"locales": {"de-DE": {"name": "jellyfin nlu labor"}}}}}
    assert parse_skill_name(payload) == "jellyfin nlu labor"


def test_parse_skill_name_absent_name_is_none():
    assert parse_skill_name({}) is None
    assert parse_skill_name({"manifest": {"publishingInformation":
                                          {"locales": {"en-US": {}}}}}) is None


# --- parse_build_status ---

def test_build_status_body_wrapped_succeeded():
    payload = {"body": {"interactionModel":
                        {"lastModified": {"status": "SUCCEEDED", "locale": "de-DE"}}}}
    assert parse_build_status(payload) == "SUCCEEDED"


def test_build_status_unwrapped_failed():
    payload = {"interactionModel":
               {"lastModified": {"status": "FAILED", "locale": "de-DE"}}}
    assert parse_build_status(payload) == "FAILED"


def test_build_status_absent_last_modified_in_progress():
    assert parse_build_status({"body": {"interactionModel": {}}}) == "IN_PROGRESS"
    assert parse_build_status({}) == "IN_PROGRESS"


def test_build_status_absent_locale_in_progress():
    payload = {"interactionModel": {"lastModified": {"status": "SUCCEEDED"}}}
    assert parse_build_status(payload) == "IN_PROGRESS"


# --- run_battery_rows ---

def test_battery_rows_spacing_and_ok_rows():
    calls = []

    def fake(utterance):
        calls.append((utterance, time.monotonic()))
        return _ok_response(utterance)

    delay = 0.05
    out = run_battery_rows(fake, _rows("u one", "u two", "u three"), delay)
    assert [r["utterance"] for r in out] == ["u one", "u two", "u three"]
    assert all(r["status"] is RowStatus.OK for r in out)
    assert out[0]["selected_intent"] == "PlaySongIntent"
    assert out[0]["slots"] == {"song": {"name": "song", "value": "u one"}}
    assert out[0]["considered_intents"] == ["PlaySongIntent", "PlayAlbumIntent"]
    assert len(calls) == 3
    gaps = [calls[i + 1][1] - calls[i][1] for i in range(len(calls) - 1)]
    assert all(g >= delay - 0.001 for g in gaps)


def test_battery_rows_maps_no_selection():
    def fake(utterance):
        if utterance == "empty":
            return {"consideredIntents": []}
        return {"selectedIntent": None,
                "consideredIntents": [{"name": "IntentForDifferentSkill"},
                                      {"name": "FallbackIntent"}]}

    out = run_battery_rows(fake, _rows("que salte", "empty"), 0)
    assert out[0]["status"] is RowStatus.NO_SELECTION
    assert out[0]["selected_intent"] is None
    assert out[0]["slots"] == {}
    assert out[0]["considered_intents"] == ["IntentForDifferentSkill", "FallbackIntent"]
    assert out[1]["status"] is RowStatus.NO_SELECTION
    assert out[1]["considered_intents"] == []


def test_battery_rows_error_after_exactly_one_retry():
    attempts = {}

    def fake(utterance):
        attempts[utterance] = attempts.get(utterance, 0) + 1
        raise RuntimeError("500")  # JF-406 class; this fake ALWAYS fails

    rows = _rows("a", "b", "c")
    out = run_battery_rows(fake, rows, 0)
    assert attempts == {"a": 2, "b": 2, "c": 2}
    assert [r["status"] for r in out] == [RowStatus.ERROR] * 3
    assert all(r["selected_intent"] is None and r["slots"] == {}
               and r["considered_intents"] == [] for r in out)


def test_battery_rows_transient_failure_recovers_on_retry():
    state = {"failed": False}

    def fake(utterance):
        if not state["failed"]:
            state["failed"] = True
            raise SmapiError("single transient 500")
        return _ok_response(utterance)

    out = run_battery_rows(fake, _rows("once"), 0)
    assert state["failed"] is True
    assert out[0]["status"] is RowStatus.OK
    assert out[0]["selected_intent"] == "PlaySongIntent"


def test_battery_rows_non_dict_response_is_error_not_abort():
    attempts = []

    def fake(utterance):
        attempts.append(utterance)
        if utterance == "bad":
            return None  # non-dict on BOTH attempts: the guard must fire twice
        return _ok_response(utterance)

    out = run_battery_rows(fake, _rows("bad", "good"), 0)
    assert attempts.count("bad") == 2
    assert "good" in attempts  # the following row is still probed
    assert out[0]["status"] is RowStatus.ERROR
    assert out[0]["selected_intent"] is None
    assert out[1]["status"] is RowStatus.OK
    assert out[1]["selected_intent"] == "PlaySongIntent"


def test_wait_for_build_timeout_expired_poll_reads_in_progress(monkeypatch):
    polls = iter([
        subprocess.TimeoutExpired(cmd="ask smapi get-skill-status", timeout=120),
        {"interactionModel": {"lastModified": {"status": "SUCCEEDED", "locale": "de-DE"}}},
    ])

    def fake_ask_json(args):
        nxt = next(polls)
        if isinstance(nxt, Exception):
            raise nxt
        return nxt

    monkeypatch.setattr(smapi_ops, "_ask_json", fake_ask_json)
    monkeypatch.setattr(smapi_ops, "POLL_INTERVAL_S", 0)
    assert smapi_ops.wait_for_build("skill", "de-DE", timeout_s=30) is True
