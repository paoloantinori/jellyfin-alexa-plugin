import hashlib
import json
from pathlib import Path

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
