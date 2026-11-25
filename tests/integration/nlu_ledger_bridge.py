"""NLU ledger observation bridge (JF-954.5).

Carries the resolved intent and slots from the test body (test_nlu.py) to the
conftest hook that writes the ledger row. It is a separate module, not a
conftest attribute, for the smapi_timeouts reason: conftest's module identity
is not import-safe across rootdir changes, and both sides must share ONE
holder object. Everything here is defensive by contract: the ledger must
never change a test outcome, so observe swallows its own failures and
records nothing rather than raising into the test.
"""

from __future__ import annotations

import warnings

_observation: dict = {"intent": None, "slots": None, "considered": []}
_fault_warned = False


def observe(intent, slots, considered=None) -> None:
    """Record what the NLU resolved for the current test, before assertions run.

    ``considered`` (JF-968) carries the full consideredIntents hypotheses
    (the {intent, slots, er} list from the ONE extractor); it defaults to
    [] for older callers.
    """
    global _fault_warned
    try:
        flattened: dict = {}
        if isinstance(slots, dict):
            for name, data in slots.items():
                value = data.get("value", "") if isinstance(data, dict) else data
                flattened[str(name)] = "" if value is None else str(value)
        _observation["intent"] = None if intent is None else str(intent)
        _observation["slots"] = flattened
        _observation["considered"] = list(considered) if considered else []
    except Exception as exc:
        # A malformed payload must never raise into the test body, but an
        # observation that silently degrades to empty would read as plausible
        # data in the row, so the first fault is warned (once per process).
        _observation["intent"] = None
        _observation["slots"] = {}
        _observation["considered"] = []
        if not _fault_warned:
            _fault_warned = True
            warnings.warn(
                f"NLU ledger bridge failed to record an observation; rows "
                f"carry null intents until fixed: {exc!r}",
                RuntimeWarning,
            )


def take():
    """Return (intent, slots, considered) for the current test and reset the holder."""
    current = (_observation["intent"], _observation["slots"],
               _observation["considered"])
    reset()
    return current


def reset() -> None:
    """Drop any observation left over from a previous test."""
    _observation["intent"] = None
    _observation["slots"] = None
    _observation["considered"] = []
