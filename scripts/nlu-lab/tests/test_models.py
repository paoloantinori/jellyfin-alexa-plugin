import hashlib
import json
from pathlib import Path

import pytest

import _paths
_paths.bootstrap()

from models import load_model_file, build_from_template, prepare_payload

REPO = Path(__file__).resolve().parents[3]
COMMITTED_DE = REPO / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel" / "model_de-DE.json"


def _bare_model():
    return {
        "languageModel": {
            "invocationName": "meine sammlung",
            "intents": [
                {"name": "PlaySongIntent", "samples": ["spiele das lied x", "spiele das lied y"]},
                {"name": "AMAZON.StopIntent"},
                {"name": "FindSongIntent", "samples": ["finde ein lied"]},
            ],
        },
        "dialog": {"intents": []},
    }


def _write(tmp_path, name, doc):
    p = tmp_path / name
    p.write_text(json.dumps(doc), encoding="utf-8")
    return p


def test_load_accepts_wrapper_and_bare_language_model(tmp_path):
    wrapper = {"interactionModel": _bare_model()}
    p = _write(tmp_path, "wrapped.json", wrapper)
    assert load_model_file(p) == wrapper
    p2 = _write(tmp_path, "bare.json", _bare_model())
    assert load_model_file(p2) == {"interactionModel": _bare_model()}


def test_non_dict_interaction_model_value_refuses(tmp_path):
    """JF-935: {"interactionModel": "oops"} used to pass the key-presence
    check and crash prepare_payload with TypeError at
    payload["interactionModel"]["languageModel"]; the one shape authority
    refuses it with ValueError (which the CLI converts into RunRefused)."""
    p = _write(tmp_path, "oops.json", {"interactionModel": "oops"})
    with pytest.raises(ValueError, match="interactionModel is str, expected a dict"):
        load_model_file(p)
    with pytest.raises(ValueError, match="interactionModel is str, expected a dict"):
        prepare_payload({"interactionModel": "oops"}, "nlu labor")


def test_non_dict_language_model_value_refuses():
    """JF-935: the bare-languageModel twin of the wrapper case refuses the
    same way instead of crashing downstream."""
    with pytest.raises(ValueError, match="languageModel is str, expected a dict"):
        prepare_payload({"languageModel": "oops"}, "nlu labor")


def test_wrapper_without_inner_language_model_refuses(tmp_path):
    """JF-935 companion: {"interactionModel": {}} and a non-dict inner
    languageModel used to crash prepare_payload downstream with KeyError /
    TypeError at payload["interactionModel"]["languageModel"]; the shape
    authority refuses them too (shape only: content stays with the
    structural validator)."""
    p = _write(tmp_path, "empty_wrapper.json", {"interactionModel": {}})
    with pytest.raises(ValueError, match="interactionModel.languageModel is NoneType"):
        load_model_file(p)
    with pytest.raises(ValueError, match="interactionModel.languageModel is int"):
        prepare_payload({"interactionModel": {"languageModel": 5}}, "nlu labor")


def test_build_from_template_is_in_memory(tmp_path, monkeypatch):
    before = COMMITTED_DE.read_bytes()
    monkeypatch.chdir(tmp_path)
    model = build_from_template("de-DE")
    assert list(tmp_path.iterdir()) == []
    assert COMMITTED_DE.read_bytes() == before
    assert model["languageModel"]["invocationName"] == "meine sammlung"


def test_prepare_payload_substitutes_lab_name_without_mutating():
    model = _bare_model()
    payload, _sha, _roster = prepare_payload(model, "nlu labor")
    assert payload["interactionModel"]["languageModel"]["invocationName"] == "nlu labor"
    assert model["languageModel"]["invocationName"] == "meine sammlung"
    assert payload["interactionModel"]["languageModel"]["intents"] is not model["languageModel"]["intents"]
    wrapped_input, sha2, _r2 = prepare_payload({"interactionModel": model}, "nlu labor")
    assert wrapped_input == payload and sha2 == _sha


def test_prepare_payload_sha256_is_canonical_digest():
    payload, sha, _roster = prepare_payload(_bare_model(), "nlu labor")
    canonical = json.dumps(payload, sort_keys=True, ensure_ascii=False)
    assert sha == hashlib.sha256(canonical.encode("utf-8")).hexdigest()
    assert len(sha) == 64


def test_intent_roster_lists_every_intent_first_sample():
    _payload, _sha, roster = prepare_payload(_bare_model(), "nlu labor")
    assert roster == [
        {"intent": "PlaySongIntent", "sample": "spiele das lied x"},
        {"intent": "AMAZON.StopIntent", "sample": None},
        {"intent": "FindSongIntent", "sample": "finde ein lied"},
    ]


def test_de_template_real_path_smoke():
    model = build_from_template("de-DE")
    payload, _sha, roster = prepare_payload(model, "nlu labor")
    lm = payload["interactionModel"]["languageModel"]
    assert lm["invocationName"] == "nlu labor"
    ints = lm["intents"]
    assert len(roster) == len(ints)
    assert len(ints) >= 60  # ~58 custom intents + AMAZON built-ins at commit time
    assert {r["intent"] for r in roster} == {i["name"] for i in ints}
    assert all(r["sample"] is None or isinstance(r["sample"], str) and r["sample"] for r in roster)
    assert any(r["sample"] is not None for r in roster)


# --- review round (2026-11-24): intents shape and the both-keys smuggle --------------------


def test_missing_intents_list_refuses_instead_of_keyerror():
    with pytest.raises(ValueError, match="intents"):
        prepare_payload({"languageModel": {}}, "lab")


def test_non_list_intents_refuses():
    with pytest.raises(ValueError, match="intents"):
        prepare_payload({"languageModel": {"intents": "oops"}}, "lab")


def test_non_mapping_intent_entries_refuse():
    with pytest.raises(ValueError, match="intents"):
        prepare_payload({"languageModel": {"intents": ["not-a-dict"]}}, "lab")


def test_both_keys_with_garbage_wrapper_refuses_not_smuggled():
    # A dict carrying BOTH a non-dict interactionModel and a valid
    # languageModel must refuse; the bare branch must not wrap the whole
    # doc and smuggle the garbage key into the payload (review round).
    model = {"interactionModel": 5, "languageModel": _bare_model()["languageModel"]}
    with pytest.raises(ValueError, match="interactionModel"):
        prepare_payload(model, "lab")


def test_bare_model_with_dialog_sibling_still_wraps_whole_doc():
    # dialog belongs INSIDE the SMAPI wrapper; the bare branch keeps
    # wrapping the whole doc for the committed-model shape.
    payload, _sha, _roster = prepare_payload(_bare_model(), "nlu labor")
    assert "dialog" in payload["interactionModel"]
    assert "interactionModel" not in payload["interactionModel"]
