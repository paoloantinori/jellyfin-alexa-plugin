"""The sample-carrier lookup: does a candidate sample already exist, and on which intent (JF-970).

A CARRIER is a sample with every ``{slot}`` placeholder reduced to ONE
anonymous token: the utterance shape the trainer sees, independent of
which slot fills it. Two samples share a carrier when Alexa could read
them as the same pattern with different slot vocabulary, which is
exactly the class ``match`` cannot surface (it takes a real utterance
and needs the spoken value in the slot vocabulary to fire).

The verdicts per (candidate, locale):

- PRESENT: an existing sample of the NAMED intent normalizes to the
  identical carrier (only checked when the candidate names an intent);
- SAME_CARRIER: another intent owns the identical carrier, reported for
  every hit with the owner intent, its sample text and each placeholder's
  slot name and declared slot type (catalog-backed types flagged);
- ABSENT: no identical carrier anywhere in the locale model.

Normalization reuses the ONE shared normalizer (``normalize.tokenize``
and ``SLOT_PLACEHOLDER_RE``); no second normalizer lives here.
"""

from __future__ import annotations

from nlu_static.effective_model import EffectiveModel, is_catalog_type
from nlu_static.normalize import (
    SLOT_PLACEHOLDER_RE,
    is_whitespace_tokenized,
    normalize_text,
    tokenize,
)

# Verdict names, exactly the three the docs describe.
PRESENT = "PRESENT"
SAME_CARRIER = "SAME_CARRIER"
ABSENT = "ABSENT"

# The anonymous slot token a placeholder reduces to. The guillemets make
# a collision with any real tokenized word (edge punctuation is stripped,
# inner punctuation survives) practically impossible.
CARRIER_SLOT_TOKEN = "‹slot›"


def carrier_key(sample: str, locale: str) -> tuple[str, ...]:
    """The carrier of one sample under the locale's tokenization rule.

    Whitespace-tokenized locales: the lit tokens of the sample with each
    placeholder replaced by one anonymous token. ja-JP (no word spaces):
    plain character-level comparison after slot reduction, following the
    fixtures subcommand's precedent of processing ja-JP without word
    tokens instead of skipping it.
    """
    if is_whitespace_tokenized(locale):
        parts: list[str] = []
        cursor = 0
        for match in SLOT_PLACEHOLDER_RE.finditer(sample):
            parts.extend(tokenize(sample[cursor : match.start()]))
            parts.append(CARRIER_SLOT_TOKEN)
            cursor = match.end()
        parts.extend(tokenize(sample[cursor:]))
        return tuple(parts)
    reduced = SLOT_PLACEHOLDER_RE.sub(CARRIER_SLOT_TOKEN, normalize_text(sample))
    # Whitespace is meaningless in a locale whose samples carry no word
    # spaces (the definition of NON_WHITESPACE_TOKENIZED_LOCALES), so the
    # character-level carrier drops it entirely instead of letting one
    # stray space split two otherwise identical carriers.
    return ("".join(reduced.split()),)


def slot_infos(sample: str, intent) -> list[dict]:
    """Every placeholder of one sample with its declared slot name and type.

    ``intent`` is an IntentSpec; a placeholder naming no declared slot
    reports an empty type (the model's own samples always declare theirs).
    """
    infos: list[dict] = []
    for match in SLOT_PLACEHOLDER_RE.finditer(sample):
        name = match.group(0)[1:-1]
        type_name = ""
        for slot in intent.slots:
            if slot.name == name:
                type_name = slot.type
                break
        infos.append(
            {"name": name, "type": type_name, "catalog": is_catalog_type(type_name)}
        )
    return infos


def analyze_candidate(
    locale: str, intent_name: str | None, sample: str, model: EffectiveModel
) -> dict:
    """One candidate's verdict row against one locale's effective model.

    The row carries the candidate echo, the verdict, the PRESENT evidence
    (the named intent's first identical-carrier sample, in model order)
    and EVERY same-carrier hit of the other intents, each with owner
    intent, sample text and per-placeholder slot name/type. Both lists
    are deterministic (model intents are name-sorted; hits are sorted by
    intent then sample).
    """
    key = carrier_key(sample, locale)
    present: dict | None = None
    hits: list[dict] = []
    for intent in model.intents:
        for existing in intent.samples:
            if carrier_key(existing, locale) != key:
                continue
            if intent_name is not None and intent.name == intent_name:
                if present is None:
                    present = {"intent": intent.name, "sample": existing}
            else:
                hits.append(
                    {
                        "intent": intent.name,
                        "sample": existing,
                        "slots": slot_infos(existing, intent),
                    }
                )
    hits.sort(key=lambda hit: (hit["intent"], hit["sample"]))
    if present is not None:
        verdict = PRESENT
    elif hits:
        verdict = SAME_CARRIER
    else:
        verdict = ABSENT
    return {
        "locale": locale,
        "intent": intent_name,
        "sample": sample,
        "verdict": verdict,
        "present": present,
        "same_carrier": hits,
    }
