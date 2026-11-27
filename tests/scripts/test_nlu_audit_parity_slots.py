"""JF-974 follow-up: the audit's cross-locale parity-slot exception.

The ElicitSlots/dialog lockstep chains cross-locale (table == every locale's
dialog == every locale's LM slots), so a slot whose SAMPLES are deliberately
single-locale wording (the JF-963 rule) must still be DECLARED everywhere.
The audit's dead-slot check exempts those slots outside their home locale,
but a parity slot with no home anywhere stays a real error.
"""

import importlib.util
import sys
from pathlib import Path

_AUDIT = Path(__file__).resolve().parents[2] / "scripts" / "nlu-audit" / "run_audit.py"
_spec = importlib.util.spec_from_file_location("run_audit", _AUDIT)
run_audit = importlib.util.module_from_spec(_spec)
sys.modules["run_audit"] = run_audit
_spec.loader.exec_module(run_audit)


def _lm(intent_slots, samples):
    return {"intents": [{"name": "ShufflePlayIntent", "slots": intent_slots, "samples": samples}]}


_ALBUM = {"name": "album", "type": "AMAZON.MusicRecording"}
_PLAYLIST = {"name": "playlist", "type": "AMAZON.SearchQuery"}


def test_parity_slot_silent_outside_home_locale():
    """The JF-974 shape: album declared in a non-en locale with no local
    samples, while the home locale (en-US) references it -> no error."""
    models = {
        "en-US": _lm([_PLAYLIST, _ALBUM], ["shuffle the album {album}", "shuffle the playlist {playlist}"]),
        "fr-FR": _lm([_PLAYLIST, _ALBUM], ["lis la playlist {playlist}"]),
    }
    assert run_audit.audit_slot_types(models) == []


def test_parity_slot_without_home_samples_still_errors():
    """The guard: if even the home locale never references the slot, the
    exemption must NOT fire (a parity slot with no home is a real dead slot)."""
    models = {
        "en-US": _lm([_PLAYLIST, _ALBUM], ["shuffle the playlist {playlist}"]),
        "fr-FR": _lm([_PLAYLIST, _ALBUM], ["lis la playlist {playlist}"]),
    }
    errors = run_audit.audit_slot_types(models)
    assert any("fr-FR" in e and "album" in e for e in errors), errors
    assert any("en-US" in e and "album" in e for e in errors), errors


def test_ordinary_dead_slot_still_errors():
    """A dead slot with no exception entry stays an error (the check's
    original purpose: accidental dead slots)."""
    models = {"en-US": _lm([_PLAYLIST, _ALBUM], ["shuffle the playlist {playlist}"])}
    models["en-US"]["intents"][0]["name"] = "SomeOtherIntent"
    errors = run_audit.audit_slot_types(models)
    assert any("album" in e for e in errors), errors
