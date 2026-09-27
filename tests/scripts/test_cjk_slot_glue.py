"""JF-638: the pytest harness for the CJK slot-glue check (#11).

The glued-sample shapes that reached SMAPI three times (JF-513 musician,
JF-326 star_rating, JF-636 speed) must fail the validator before any deploy.
The check lives inside validate_single_model (error-level), so the harness
feeds synthetic languageModels and asserts the error list; no repo mutation.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts"))

from test_elicit_checker import _models  # noqa: E402
from validate_interaction_models import validate_single_model  # noqa: E402


def _lm(samples: list[str]) -> dict:
    return {
        "invocationName": "test",
        "intents": [
            {"name": "PlaySongIntent", "samples": list(samples), "slots": []},
        ],
    }


def _glue_errors(samples: list[str]) -> list[str]:
    errors, _warnings = validate_single_model("ja-JP", _lm(samples))
    return [e for e in errors if "JF-638" in e]


# --- Dirty shapes: every historical incident plus one shape per CJK block ---


def test_musician_glued_particle_errors():
    # JF-513 exact shape: particle directly after the closing brace.
    errors = _glue_errors(["{musician}を再生して"])
    assert len(errors) == 1, errors
    assert "U+3092 directly after '}'" in errors[0], errors


def test_star_rating_glued_kanji_errors():
    # JF-326 exact shape: kanji directly before the opening brace.
    errors = _glue_errors(["星{star_rating}で評価して"])
    assert len(errors) == 1, errors
    assert "U+661F directly before '{'" in errors[0], errors


def test_speed_glued_both_sides_errors():
    # JF-636 shape: glued on both sides, one error naming both.
    errors = _glue_errors(["速度{speed}にして"])
    assert len(errors) == 1, errors
    assert "directly before '{'" in errors[0], errors
    assert "directly after '}'" in errors[0], errors


def test_katakana_and_cjk_punctuation_glue_error():
    errors = _glue_errors(["テスト{album}、再生"])
    assert len(errors) == 1, errors
    # ト = U+30C8 (verified against unicodedata: TE is U+30C6, TO is U+30C8).
    assert "U+30C8 directly before '{'" in errors[0], errors
    assert "U+3001 directly after '}'" in errors[0], errors


def test_ideographic_space_is_not_a_separator():
    # The ja-JP template header bans U+3000 in samples entirely; only a
    # regular ASCII space may separate a slot from CJK text.
    errors = _glue_errors(["{musician}　を再生して"])
    assert len(errors) == 1, errors


def test_error_names_intent_and_locale():
    errors = _glue_errors(["{musician}を再生して"])
    assert errors[0].startswith("  [ja-JP]"), errors
    assert "Intent 'PlaySongIntent'" in errors[0], errors


# --- Clean shapes: the documented JF-513 rule and the check's exact scope ---


def test_spaced_forms_are_clean():
    samples = [
        "{musician} を再生して",
        "星 {star_rating} で評価して",
        "曲 {musician} を流して",
        "{musician}",  # slot at string start and end: no neighbor at all
    ]
    errors, warnings = validate_single_model("ja-JP", _lm(samples))
    assert errors == [], errors


def test_adjacent_ascii_slots_are_not_flagged():
    # Two slot refs glued to each other are ASCII adjacency, outside the
    # directed trigger condition (CJK adjacency only).
    errors, _ = validate_single_model("ja-JP", _lm(["{album}{musician}"]))
    assert errors == [], errors


def test_latin_glue_is_out_of_class():
    # Latin-script glue has no live InvalidCharInSamples incident; the class
    # is deliberately CJK-only to keep the error level false-positive-free.
    errors, _ = validate_single_model("en-US", _lm(["play{album} now"]))
    assert errors == [], errors


# --- The real tree: all 17 committed models must stay clean -------------


def test_all_committed_models_have_no_glued_slots():
    models = _models()
    assert len(models) == 17, len(models)
    for locale, lm in models.items():
        errors, _ = validate_single_model(locale, lm)
        assert not any("JF-638" in e for e in errors), errors
