"""The ONE shared text normalizer for every nlu_static tool (JF-954).

Later subtasks must import these functions instead of writing a second
normalizer. Loaders store values exactly as given; normalization happens at
comparison time so a diff can still show the raw spellings.
"""

from __future__ import annotations

import re
import unicodedata

# The ONE slot-placeholder regex: a ``{slot_name}`` reference inside an
# intent sample. Carrier computation (tokens after placeholder removal) and
# the validator's carrier lints share this single definition so the two
# placeholder shapes cannot drift.
SLOT_PLACEHOLDER_RE = re.compile(r"\{[^}]*\}")

# Locales whose samples carry no word spaces, so whitespace tokenization is
# meaningless and every token-dependent tool must print an explicit SKIP line
# (parent task JF-954 rule: silent skips are forbidden). Today that is ja-JP
# only.
NON_WHITESPACE_TOKENIZED_LOCALES: frozenset[str] = frozenset({"ja-JP"})


def normalize_text(s: str) -> str:
    """Return ``s`` normalized for comparison.

    Unicode NFC first, then ``casefold()``, then every run of whitespace
    collapsed to one ASCII space and the ends stripped. NFC composes
    decomposed accents, so ``"cafe\\u0301"`` and ``"caf\\u00e9"`` normalize
    equal; casefold is stronger than ``lower()`` (German sharp s folds to
    ss, so ``STRASSE`` and ``stra\\u00dfe`` normalize equal). The
    typographic right single quote U+2019 maps to the ASCII apostrophe
    first: taggers and web sources paste it into titles, NFC and casefold
    do not unify the two codepoints, and an elision like ``l'album`` must
    compare equal in both spellings.
    """
    folded_quotes = s.replace("’", "'")
    nfc = unicodedata.normalize("NFC", folded_quotes)
    return " ".join(nfc.casefold().split())


def tokenize(s: str) -> list[str]:
    """Split normalized text into word tokens.

    Leading and trailing punctuation is stripped from each token using the
    Unicode punctuation categories (``unicodedata.category(ch)`` starting
    with ``P``); punctuation INSIDE a token survives, so ``l'album`` and
    ``schlaf-timer`` stay single tokens. A token that is all punctuation
    collapses to nothing and is dropped.
    """
    tokens: list[str] = []
    for raw in normalize_text(s).split(" "):
        token = _strip_edge_punctuation(raw)
        if token:
            tokens.append(token)
    return tokens


def _strip_edge_punctuation(token: str) -> str:
    """Cut category-P characters from both ends of one token."""
    start = 0
    end = len(token)
    while start < end and unicodedata.category(token[start]).startswith("P"):
        start += 1
    while end > start and unicodedata.category(token[end - 1]).startswith("P"):
        end -= 1
    return token[start:end]


def is_whitespace_tokenized(locale: str) -> bool:
    """True when the locale's samples tokenize on word spaces.

    False exactly for the locales in ``NON_WHITESPACE_TOKENIZED_LOCALES``:
    False for ja-JP, True for the other 16. A tool that depends on word
    tokens checks this and prints ``skip_line(locale)`` instead of
    producing output for that locale.
    """
    return locale not in NON_WHITESPACE_TOKENIZED_LOCALES


def skip_line(locale: str) -> str | None:
    """The mandated SKIP line for a locale word tokens cannot serve, or None.

    The ONE producer of the explicit-skip contract (parent task rule:
    silent skips are forbidden): ``SKIP ja-JP: no whitespace tokenization``
    for the non-whitespace locales, None for every locale a token tool may
    process. Callers print the returned line verbatim and move on.
    """
    if is_whitespace_tokenized(locale):
        return None
    return f"SKIP {locale}: no whitespace tokenization"
