"""The effective-model builder: a locale model as the vocabulary Amazon actually trains on (JF-954).

The committed ``model_<locale>.json`` carries a small static seed for each
catalog-backed slot type (SeriesName, for example, ships ten seed titles).
On a user's live skill, catalog sync replaces that seed with the user's
library values, so a check that reads only the committed model sees a
vocabulary that does not exist on the live skill. The EFFECTIVE model is the
committed model with the values of catalog-backed types replaced by a
catalog snapshot; without a snapshot it is the committed model verbatim,
with every type labeled STATIC or CATALOG.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

VocabKind = Literal["STATIC", "CATALOG"]
ModelSource = Literal["committed", "snapshot"]

# Slot type names the catalog sync REPLACES with the user's library values.
# Source of truth: Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogSlotTypes.cs
# (the CatalogSlotTypeNames dictionary). A unit test greps that C# file and
# fails when the two name sets diverge; change both in the same commit.
CATALOG_TYPE_NAMES: tuple[str, ...] = (
    "AlbumName",
    "AudiobookTitle",
    "JellyfinArtist",
    "MovieTitle",
    "SeriesName",
    "SongTitle",
)

_CATALOG_TYPE_SET = frozenset(CATALOG_TYPE_NAMES)

# Pinned classification tables for classify_slot_type. FREE holds the
# free-text and entity builtins; NUMERIC and STRUCTURED hold their builtin
# families; every custom type is CLOSED. Any other AMAZON.* name is
# UNKNOWN_BUILTIN and callers report it, so the table gets maintained
# instead of silently guessing.
FREE_BUILTIN_TYPES: frozenset[str] = frozenset(
    {
        "AMAZON.SearchQuery",
        "AMAZON.MusicRecording",
        "AMAZON.Musician",
        "AMAZON.Genre",
        "AMAZON.Person",
    }
)
NUMERIC_TYPES: frozenset[str] = frozenset({"AMAZON.NUMBER"})
STRUCTURED_TYPES: frozenset[str] = frozenset({"AMAZON.DURATION", "AMAZON.TIME"})

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_MODELS_DIR = REPO_ROOT / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"

SNAPSHOT_SCHEMA = 1
SNAPSHOT_GLOB_KEY = "*"


@dataclass(frozen=True)
class SlotSpec:
    """One slot of an intent: its name and the slot type it declares."""

    name: str
    type: str


@dataclass(frozen=True)
class IntentSpec:
    """One intent of a locale model: name, samples and declared slots."""

    name: str
    samples: tuple[str, ...]
    slots: tuple[SlotSpec, ...]


@dataclass(frozen=True)
class SlotTypeVocab:
    """The effective vocabulary of one slot type.

    ``kind`` is STATIC for a plain custom type or CATALOG for a
    catalog-backed one; kind follows the TYPE and never changes when a
    snapshot is applied. Each entry is a ``(canonical, synonyms)`` pair;
    values are stored exactly as given and normalization happens at
    comparison time.
    """

    name: str
    kind: VocabKind
    entries: tuple[tuple[str, tuple[str, ...]], ...]


@dataclass(frozen=True)
class EffectiveModel:
    """A locale model as the vocabulary the live skill trains on.

    ``source`` is ``"snapshot"`` when the snapshot actually replaced at
    least one type's entries, ``"committed"`` otherwise (including when a
    snapshot was passed but replaced nothing). ``snapshot_types`` lists the
    names the snapshot replaced; ``notes`` carries the deterministic human
    messages, currently one per snapshot type the locale model does not
    declare.
    """

    locale: str
    intents: tuple[IntentSpec, ...]
    types: tuple[SlotTypeVocab, ...]
    source: ModelSource
    snapshot_types: tuple[str, ...] = ()
    notes: tuple[str, ...] = ()

    def type_vocab(self, name: str) -> SlotTypeVocab | None:
        """The effective vocab of one slot type, or None when undeclared."""
        for vocab in self.types:
            if vocab.name == name:
                return vocab
        return None


def load_locale_model(locale: str, models_dir: str | Path | None = None) -> dict:
    """Load ``model_<locale>.json`` and return its ``languageModel`` dict.

    Accepts a bare document with a top-level ``languageModel`` key, one
    wrapped in an ``interactionModel`` object, and a document that IS the
    language model itself. Raises FileNotFoundError for a missing locale
    file and ValueError for a readable file without a usable model.
    """
    directory = Path(models_dir) if models_dir is not None else DEFAULT_MODELS_DIR
    path = directory / f"model_{locale}.json"
    doc = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(doc, dict):
        raise ValueError(f"{path}: top level must be a JSON object")
    wrapper = doc.get("interactionModel")
    container = wrapper if isinstance(wrapper, dict) else doc
    model = container.get("languageModel")
    if isinstance(model, dict):
        return model
    if isinstance(doc.get("intents"), list):
        # The document IS a language model (no wrapper, no languageModel key).
        return doc
    raise ValueError(f"{path}: no languageModel section found")


def build_effective_model(
    locale: str,
    snapshot: dict | None = None,
    models_dir: str | Path | None = None,
) -> EffectiveModel:
    """Build the effective model for one locale.

    Without a snapshot every declared type keeps its committed values: kind
    CATALOG for the names in CATALOG_TYPE_NAMES, STATIC otherwise. With a
    snapshot, each type the snapshot names for this locale has its entries
    REPLACED by the snapshot entries; types absent from the snapshot keep
    committed values. A snapshot type the locale model does not declare is
    ignored and listed in EffectiveModel.notes. Pass ``models_dir`` to read
    synthetic models from a test directory; ``snapshot`` is a parsed
    snapshot dict, normally from load_snapshot.
    """
    model = load_locale_model(locale, models_dir)
    intents = _parse_intents(model)
    committed = _parse_types(model)

    snapshot_types: list[str] = []
    notes: list[str] = []
    if snapshot is not None:
        table = _resolve_locale_table(snapshot, locale)
        if table is None:
            notes.append(
                f"snapshot has no entry for locale {locale} and no "
                f"{SNAPSHOT_GLOB_KEY!r} fallback; committed values kept"
            )
        else:
            if not isinstance(table, dict):
                raise ValueError(f"snapshot locales.{locale} must be an object keyed by slot type name")
            for type_name in sorted(table):
                entries = _parse_snapshot_entries(
                    table[type_name], f"snapshot locales.{locale}.{type_name}"
                )
                if type_name in committed:
                    committed[type_name]["entries"] = entries
                    snapshot_types.append(type_name)
                else:
                    notes.append(
                        f"snapshot type {type_name!r} is not declared by the {locale} model; ignored"
                    )

    source: ModelSource = "snapshot" if snapshot_types else "committed"
    types = tuple(
        SlotTypeVocab(
            name=name,
            kind=committed[name]["kind"],
            entries=committed[name]["entries"],
        )
        for name in sorted(committed)
    )
    return EffectiveModel(
        locale=locale,
        intents=intents,
        types=types,
        source=source,
        snapshot_types=tuple(sorted(snapshot_types)),
        notes=tuple(sorted(notes)),
    )


def classify_slot_type(type_name: str, effective_model: EffectiveModel) -> str:
    """Classify a slot type name with the pinned JF-954 table.

    FREE = AMAZON.SearchQuery, AMAZON.MusicRecording, AMAZON.Musician,
    AMAZON.Genre, AMAZON.Person; NUMERIC = AMAZON.NUMBER; STRUCTURED =
    AMAZON.DURATION and AMAZON.TIME; CLOSED = every custom type (kind
    STATIC or CATALOG). Any other AMAZON.* name returns UNKNOWN_BUILTIN:
    callers treat it as FREE for behavior but must report it, so this table
    gets maintained instead of silently guessing. A custom name this model
    does not declare still returns CLOSED (another locale may declare it);
    the model lookup confirms declared membership.
    """
    if type_name in FREE_BUILTIN_TYPES:
        return "FREE"
    if type_name in NUMERIC_TYPES:
        return "NUMERIC"
    if type_name in STRUCTURED_TYPES:
        return "STRUCTURED"
    if type_name.startswith("AMAZON."):
        return "UNKNOWN_BUILTIN"
    if effective_model.type_vocab(type_name) is not None:
        return "CLOSED"
    return "CLOSED"


def load_snapshot(path: str | Path) -> dict:
    """Load and validate a catalog snapshot file and return the parsed dict.

    The snapshot format (schema 1) maps locales to slot type names to lists
    of ``{"value": ..., "synonyms": [...]}`` entries; the locale key
    ``"*"`` applies to every locale without an entry of its own. Rejects an
    unknown or missing schema number, a non-list type entry, and an entry
    without a string ``value``, each with a ValueError naming the offending
    path. Values are stored as given; normalization happens at comparison
    time.
    """
    snapshot_path = Path(path)
    try:
        doc = json.loads(snapshot_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as err:
        raise ValueError(f"{snapshot_path}: not valid JSON ({err.msg} at line {err.lineno})") from err
    if not isinstance(doc, dict):
        raise ValueError(f"{snapshot_path}: top level must be a JSON object")
    schema = doc.get("schema")
    if isinstance(schema, bool) or schema != SNAPSHOT_SCHEMA:
        raise ValueError(f"{snapshot_path}: snapshot schema must be {SNAPSHOT_SCHEMA}, got {schema!r}")
    locales = doc.get("locales")
    if not isinstance(locales, dict) or not locales:
        raise ValueError(f"{snapshot_path}: 'locales' must be a non-empty object keyed by locale")
    for locale_key, table in locales.items():
        if not isinstance(locale_key, str) or not isinstance(table, dict):
            raise ValueError(
                f"{snapshot_path}: locales.{locale_key!r} must be an object keyed by slot type name"
            )
        for type_name, entries in table.items():
            _parse_snapshot_entries(entries, f"{snapshot_path}: locales.{locale_key}.{type_name}")
    return doc


def _resolve_locale_table(snapshot: dict, locale: str) -> dict | None:
    """The snapshot's table for one locale: its own entry, else the "*" fallback."""
    locales = snapshot.get("locales", {})
    if locale in locales:
        return locales[locale]
    return locales.get(SNAPSHOT_GLOB_KEY)


def _parse_snapshot_entries(raw: object, context: str) -> tuple[tuple[str, tuple[str, ...]], ...]:
    """Validate one type's entry list; ``context`` names it in every error."""
    if not isinstance(raw, list):
        raise ValueError(f"{context} must be a list of entries")
    entries: list[tuple[str, tuple[str, ...]]] = []
    for index, entry in enumerate(raw):
        if not isinstance(entry, dict) or not isinstance(entry.get("value"), str):
            raise ValueError(f"{context}[{index}] must have a string 'value'")
        synonyms = entry.get("synonyms", [])
        if not isinstance(synonyms, list) or not all(isinstance(s, str) for s in synonyms):
            raise ValueError(f"{context}[{index}] 'synonyms' must be a list of strings")
        entries.append((entry["value"], tuple(synonyms)))
    return tuple(entries)


def _parse_intents(model: dict) -> tuple[IntentSpec, ...]:
    """Read intents sorted by name for a deterministic model identity."""
    intents: list[IntentSpec] = []
    for raw in model.get("intents", []):
        name = raw.get("name", "")
        samples = tuple(raw.get("samples", []))
        slots = tuple(
            SlotSpec(name=slot.get("name", ""), type=slot.get("type", ""))
            for slot in raw.get("slots", [])
        )
        intents.append(IntentSpec(name=name, samples=samples, slots=slots))
    return tuple(sorted(intents, key=lambda intent: intent.name))


def _parse_types(model: dict) -> dict[str, dict]:
    """Read the declared types into name -> {kind, entries} records."""
    committed: dict[str, dict] = {}
    for raw in model.get("types", []):
        name = raw.get("name")
        if not isinstance(name, str):
            raise ValueError("model has a type entry without a string name")
        entries: list[tuple[str, tuple[str, ...]]] = []
        for value in raw.get("values", []):
            value_obj = value.get("name") if isinstance(value, dict) else None
            if not isinstance(value_obj, dict) or not isinstance(value_obj.get("value"), str):
                raise ValueError(f"model type {name!r} has a value entry without a string name.value")
            synonyms = value_obj.get("synonyms") or ()
            entries.append((value_obj["value"], tuple(synonyms)))
        kind: VocabKind = "CATALOG" if name in _CATALOG_TYPE_SET else "STATIC"
        committed[name] = {"kind": kind, "entries": tuple(entries)}
    return committed
