"""The real-utterance matcher and the fixture-row classification (JF-954.4).

``match_intents`` asks "for which intents does at least one sample have a
reading that covers this real utterance"; ``classify_fixture_row`` turns
that answer into the UNCONTESTED / CONTESTED / NO_SAMPLE_MATCH triage
classes the JF-954.6 report consumes. Both are pure functions of their
inputs; matching uses the UTTERANCE mode rule table of
``patterns.matches``, where NUMERIC and STRUCTURED deliberately
over-approximate, so a hit means "a reading exists", not "Amazon will
choose it".
"""

from __future__ import annotations

from nlu_static import patterns
from nlu_static.effective_model import EffectiveModel
from nlu_static.normalize import tokenize


class PreparedMatcher:
    """A model compiled once for many utterance matches (the CLI batch path).

    ``match_intents`` and ``classify_fixture_row`` accept one so a
    classify-fixtures run compiles the model a single time instead of
    once per row; passing None recompiles per call.
    """

    __slots__ = ("compiled", "entry_tokens")

    def __init__(self, effective_model: EffectiveModel) -> None:
        self.compiled, _unclassified = patterns.compile_model_patterns(effective_model)
        self.entry_tokens = patterns.closed_entry_tokens(effective_model)


def _match_utterance_tokens(witness: tuple, prepared: PreparedMatcher) -> dict[str, dict]:
    """The per-intent best hits over one tokenized utterance."""
    best: dict[str, dict] = {}
    for c in prepared.compiled:
        if not patterns.matches(c, witness, prepared.entry_tokens, mode=patterns.UtteranceMode):
            continue
        current = best.get(c.intent)
        if current is None or c.specificity > current["specificity"]:
            best[c.intent] = {
                "intent": c.intent,
                "sample": c.sample,
                "specificity": c.specificity,
            }
    return best


def match_intents(
    utterance: str,
    effective_model: EffectiveModel,
    prepared: PreparedMatcher | None = None,
) -> list[dict]:
    """Match a real utterance against every sample pattern of the model.

    Returns one hit per intent that has at least one matching sample: the
    sample with the highest specificity (the number of literal tokens in
    the sample; ties keep the model's sample order), each hit carrying
    ``intent``, ``sample`` and ``specificity``. The list is sorted by
    specificity descending, then intent name.
    """
    tokens = tokenize(utterance)
    if not tokens:
        return []
    # witness items for a real utterance: plain literal tokens
    witness = tuple(("lit", token) for token in tokens)
    if prepared is None:
        prepared = PreparedMatcher(effective_model)
    best = _match_utterance_tokens(witness, prepared)
    return sorted(best.values(), key=lambda hit: (-hit["specificity"], hit["intent"]))


def classify_fixture_row(
    row: dict,
    effective_model: EffectiveModel,
    prepared: PreparedMatcher | None = None,
) -> dict:
    """Classify one fixture row against the effective model.

    ``row`` carries ``utterance`` and ``expected_intent`` (an ``A|B``
    alternative list is accepted, as the fixture format defines). One of:

    - UNCONTESTED: only expected intents match;
    - CONTESTED: an expected intent matches and at least one other intent
      matches too; ``rivals`` lists the others and
      ``rival_at_least_as_specific`` is true when the best rival is at
      least as specific as the best expected hit;
    - NO_SAMPLE_MATCH: no expected intent has a matching sample; the row
      relies on trainer generalization or on vocabulary absent from the
      effective model; ``rivals`` lists everything that does match.
    """
    utterance = row.get("utterance", "")
    expected_raw = row.get("expected_intent", "")
    expected = {
        name.strip() for name in str(expected_raw).split("|") if name.strip()
    }
    hits = match_intents(utterance, effective_model, prepared)
    expected_hits = [hit for hit in hits if hit["intent"] in expected]
    rivals = [hit for hit in hits if hit["intent"] not in expected]
    if not expected_hits:
        classification = "NO_SAMPLE_MATCH"
        at_least_as_specific = False
    elif rivals:
        classification = "CONTESTED"
        best_expected = max(hit["specificity"] for hit in expected_hits)
        at_least_as_specific = any(
            hit["specificity"] >= best_expected for hit in rivals
        )
    else:
        classification = "UNCONTESTED"
        at_least_as_specific = False
    return {
        "classification": classification,
        "rivals": [dict(hit) for hit in rivals],
        "rival_at_least_as_specific": at_least_as_specific,
    }
