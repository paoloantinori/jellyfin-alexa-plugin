#!/usr/bin/env python3
"""Generate Alexa interaction model JSON from compact YAML templates.

Usage:
    python scripts/generate_interaction_model.py [locale]

    locale: The locale to generate (e.g. it-IT, en-US). Defaults to it-IT.

Reads:  Alexa/InteractionModel/templates/<locale>.yaml
Writes: Alexa/InteractionModel/model_<locale>.json

A template may declare its intents in either form (not both):

- Legacy grouped sections (used by it-IT): `static_intents` (name-only),
  `explicit_intents` (verbatim samples), `templates` (vocabulary-expanded
  samples). Intents are emitted in that section order; static intents
  cannot be interleaved with custom ones.

- Ordered `intents` list (the standard form, used by 16 of 17 locales): each
  item is either a bare string (a name-only intent) or a mapping with
  `name` plus optional `samples` (verbatim list), `templates`
  (vocabulary-expanded; template strings without {vocab} refs pass through
  unchanged), `slots`, and `mirror` (see below). The authored list order is
  preserved exactly, so static AMAZON.* intents can sit between custom
  intents the way the committed hand-grown models have them.

Mirror directive (JF-855): an ordered intent may declare `mirror` instead
of `samples`/`templates`:

    - name: PlayEpisodeIntent
      slots: [...]
      mirror:
        locale: es-ES
        exclude:
          - que reproduzca ...   # rows the reference carries and this locale does not

The samples become the reference locale's SAME intent samples, in the
reference's order, minus the `exclude` rows. This relaxes the JF-316
verbatim-transcription decision for DECLARED-MIRROR families only: every
non-mirrored intent of the sibling stays verbatim, and the reference locale
keeps hand-authoring its own rows. Mirroring is then enforced at generation
time by construction - a reference row the sibling did not exclude is
inherited on the sibling's next regen, so the per-family drift lints (the
JF-844.1 es-trio class) retire. The generator raises on: a missing reference
template or reference intent, a mirrored reference with no samples, an
`exclude` row absent from the reference samples (a stale divergence), a
mirror producing zero samples, `mirror` combined with `samples`/`templates`,
an unknown `mirror` key, and a mirror cycle (A mirroring B mirroring A, or a
locale mirroring itself). The intent's emitted `samples` sit at the position
of the `mirror` key in the YAML, the same rule as `samples`/`templates`.
Chains are allowed (A mirrors B mirrors C); the cycle guard bounds them.
`build_model` takes the building locale so the directive can resolve its
reference; templates with no `mirror` key build identically without it.

Shared sections: `vocabulary` (Cartesian-product word sets referenced from
templates as {vocab_name}), `types` (custom slot types), `dialog` and
`prompts` (passed through verbatim, emitted in that order after
languageModel).

Byte-identity contract: `json.dump(..., indent=2, ensure_ascii=False)` plus
a trailing newline. Where the committed JSON has a key order (an intent
mixing name/samples/slots vs name/slots/samples, or a type value leading
with `id` vs with `name`), the template reproduces it: ordered-intent
entries and dict type values emit their keys in YAML order, with the type
`name` subtree taking the position of the `value` key.

Requires: pyyaml (pip install pyyaml)
"""

import itertools
import json
import re
import sys
from pathlib import Path

import yaml

# Regex to find {vocab_name} references in templates (but not {slot} refs
# that are Alexa slot types like AMAZON.SearchQuery).
# Slot names are lowercase alphanumeric + underscore.
# Vocab names match the same pattern but must exist in the vocabulary section.
SLOT_RE = re.compile(r"\{(\w+)\}")


def expand_template(template: str, vocabulary: dict[str, list[str]]) -> list[str]:
    """Expand a template string by substituting vocabulary references.

    For each vocab reference found in the template, generate a Cartesian
    product of all possible substitutions. Slot references (like {song})
    that don't match any vocabulary key are preserved as-is.
    """
    # Find all {ref} in the template
    refs = SLOT_RE.findall(template)

    # Separate vocab refs from slot refs
    vocab_refs = [(i, ref) for i, ref in enumerate(refs) if ref in vocabulary]

    if not vocab_refs:
        # No vocab refs to expand — return template as-is
        return [template]

    # Build the Cartesian product of all vocab values
    vocab_names = [ref for _, ref in vocab_refs]
    vocab_value_lists = [vocabulary[name] for name in vocab_names]

    results = []
    for combo in itertools.product(*vocab_value_lists):
        result = template
        for (_, ref_name), value in zip(vocab_refs, combo):
            # Replace only the first occurrence of each vocab ref per combo
            result = result.replace("{" + ref_name + "}", value, 1)
        results.append(result)

    return results


def generate_samples(templates: list[str], vocabulary: dict[str, list[str]],
                     slot_names: frozenset[str] = frozenset()) -> list[str]:
    """Generate all sample utterances from a list of templates.

    After expansion, every remaining {ref} in a produced sample must be one
    of the intent's declared slot names: a typo'd vocabulary reference
    (e.g. {definite_artist_verbs} where the set is definite_artist_verb)
    would otherwise survive into the model as a literal braced sample that
    never matches speech, invisibly multiplied across the Cartesian
    product. The check runs on the expanded string, so refs introduced by
    vocabulary values (book_noun embedding {book}) are validated too.
    """
    samples = []
    for template in templates:
        for sample in expand_template(template, vocabulary):
            for ref in SLOT_RE.findall(sample):
                if ref not in slot_names:
                    raise ValueError(
                        f"sample {sample!r} references {{{ref}}}, which is "
                        "neither a vocabulary set nor a declared slot of "
                        "this intent (vocabulary sets: "
                        f"{sorted(vocabulary)}; slots: {sorted(slot_names)})"
                    )
            samples.append(sample)
    return samples


TYPE_VALUE_KEYS = frozenset({"value", "id", "synonyms"})


def _slot_names_of(intent_config: dict) -> frozenset[str]:
    """Declared slot names of an intent config (the {ref} validation set)."""
    return frozenset(
        s["name"] for s in intent_config.get("slots", []) if isinstance(s, dict)
    )


def _reject_unknown_keys(config: dict, allowed: set, where: str) -> None:
    """Raise on any key outside `allowed` (the silent-drop guard)."""
    unknown = set(config) - allowed
    if unknown:
        raise ValueError(
            f"unknown key(s) {sorted(unknown)} in {where} (allowed: {sorted(allowed)})"
        )


def build_type_value(v) -> dict:
    """Build one Alexa type-value entry, preserving the YAML key order.

    Plain strings become {"name": {"value": <str>}}. For dict values, the
    emitted key order follows the YAML order: `id` (and any other key)
    keeps its position, while the `name` subtree ({value, synonyms}) is
    emitted at the position of the `value` key. Committed hand-grown
    models are inconsistent here (some values lead with `id`, others with
    `name`), so the template must be able to reproduce either order.
    `synonyms` always lands inside `name`, right after `value`, and an
    explicitly empty `synonyms: []` is preserved (it appears in committed
    models).
    """
    if isinstance(v, str):
        return {"name": {"value": v}}
    unknown = set(v) - TYPE_VALUE_KEYS
    if unknown:
        raise ValueError(
            f"unknown key(s) {sorted(unknown)} in type value {v!r} "
            f"(allowed: {sorted(TYPE_VALUE_KEYS)})"
        )
    if "value" not in v:
        raise ValueError(
            f"type value dict must have a 'value' key: {v!r} (a plain "
            "string is the shorthand for a value-only entry)"
        )
    entry = {}
    for key in v:
        if key == "value":
            name_obj = {"value": v["value"]}
            if "synonyms" in v:
                name_obj["synonyms"] = v["synonyms"]
            entry["name"] = name_obj
        elif key == "synonyms":
            continue  # folded into the name subtree above
        else:
            entry[key] = v[key]
    return entry


def build_ordered_intent(item, vocabulary: dict[str, list[str]], locale: str | None,
                         mirror_stack: tuple[str, ...]) -> dict:
    """Build one intent from an ordered `intents` list entry.

    A bare string becomes a name-only intent. A mapping requires `name`
    and may carry `samples` (emitted verbatim, in order), `templates`
    (vocabulary-expanded; expansions are appended after the verbatim
    samples), `mirror` (JF-855: samples sourced from a reference locale's
    same intent, see the module docstring), and `slots` (verbatim, key
    order preserved). `mirror` excludes `samples`/`templates`: exactly one
    sample source per intent.

    As with type values, the emitted key order follows the YAML document
    order (committed hand-grown models mix `name, samples, slots` and
    `name, slots, samples`). The merged samples list is emitted once, at
    the position of whichever of `samples`/`templates`/`mirror` appears
    first in the YAML.
    """
    if isinstance(item, str):
        return {"name": item}
    name = item.get("name")
    if not name:
        raise ValueError(f"ordered intents entry missing 'name': {item!r}")
    slot_names = _slot_names_of(item)
    samples = list(item.get("samples", []))
    if "templates" in item:
        samples.extend(generate_samples(item["templates"], vocabulary, slot_names))
    if "mirror" in item:
        if "samples" in item or "templates" in item:
            raise ValueError(
                f"ordered intent {name!r} declares `mirror` together with "
                "`samples`/`templates`; exactly one sample source is allowed"
            )
        samples = resolve_mirrored_samples(
            item["mirror"], name, locale, mirror_stack, slot_names
        )
    has_samples = "samples" in item or "templates" in item or "mirror" in item

    intent: dict = {}
    for key in item:
        if key == "name":
            intent["name"] = name
        elif key in ("samples", "templates", "mirror"):
            if has_samples and "samples" not in intent:
                intent["samples"] = samples
        elif key == "slots":
            intent["slots"] = item["slots"]
        else:
            raise ValueError(
                f"unknown key {key!r} in ordered intent {name!r} "
                "(allowed: name, samples, templates, mirror, slots)"
            )
    return intent


MIRROR_KEYS = frozenset({"locale", "exclude"})


def load_template_config(locale: str) -> dict:
    """Load a sibling template by locale (the mirror reference resolver)."""
    template_path = MODELS_DIR / "templates" / f"{locale}.yaml"
    if not template_path.is_file():
        raise ValueError(f"mirror reference template not found: {template_path}")
    with open(template_path) as f:
        return yaml.safe_load(f)


def resolve_mirrored_samples(mirror: dict, intent_name: str, locale: str | None,
                             mirror_stack: tuple[str, ...],
                             slot_names: frozenset[str] = frozenset()) -> list[str]:
    """Resolve a `mirror` directive into the sibling's sample list (JF-855).

    The reference locale's own template is built (recursively, so a
    reference may itself mirror a third locale) and its same-named
    intent's samples are returned in the reference's order, minus the
    declared `exclude` rows. Every drift shape the retired per-family
    lints caught after the fact is an error here at generation time:
    missing reference template/intent, a sample-less reference, a stale
    `exclude` row, an empty result, a bad directive shape, and mirror
    cycles (the stack carries the locales already being built). The
    returned samples run the same {ref}-vs-declared-slots guard as
    generate_samples: a reference row using a slot the mirroring intent
    does not declare is the same silent-broken-sample class.
    """
    if locale is None:
        raise ValueError(
            f"intent {intent_name!r} declares `mirror`; build_model needs the "
            "building locale to resolve the reference template"
        )
    if not isinstance(mirror, dict):
        raise ValueError(
            f"`mirror` of intent {intent_name!r} must be a mapping with "
            f"locale/exclude keys, got {mirror!r}"
        )
    _reject_unknown_keys(mirror, MIRROR_KEYS, f"{locale} intent {intent_name!r} mirror")
    ref_locale = mirror.get("locale")
    if not isinstance(ref_locale, str) or not ref_locale:
        raise ValueError(
            f"`mirror.locale` of intent {intent_name!r} in {locale} must be a "
            f"non-empty string, got {ref_locale!r}"
        )
    if ref_locale == locale or ref_locale in mirror_stack:
        chain = " -> ".join((*mirror_stack, locale, ref_locale))
        raise ValueError(f"mirror cycle in intent {intent_name!r}: {chain}")

    ref_model = build_model(
        load_template_config(ref_locale),
        locale=ref_locale,
        _mirror_stack=(*mirror_stack, locale),
    )
    ref_intent = next(
        (i for i in ref_model["languageModel"]["intents"] if i["name"] == intent_name),
        None,
    )
    if ref_intent is None:
        raise ValueError(
            f"`mirror` of intent {intent_name!r} in {locale}: reference locale "
            f"{ref_locale} has no {intent_name!r} intent"
        )
    ref_samples = ref_intent.get("samples", [])
    if not ref_samples:
        raise ValueError(
            f"`mirror` of intent {intent_name!r} in {locale}: reference locale "
            f"{ref_locale} carries the intent with no samples"
        )

    exclude = mirror.get("exclude", [])
    if not isinstance(exclude, list) or not all(isinstance(r, str) for r in exclude):
        raise ValueError(
            f"`mirror.exclude` of intent {intent_name!r} in {locale} must be a "
            f"list of sample strings, got {exclude!r}"
        )
    exclude_set = set(exclude)
    stale = sorted(exclude_set - set(ref_samples))
    if stale:
        raise ValueError(
            f"`mirror.exclude` of intent {intent_name!r} in {locale} lists row(s) "
            f"{stale} that {ref_locale}'s {intent_name!r} no longer carries; "
            "update the exclude list (the divergence is stale)"
        )
    samples = [s for s in ref_samples if s not in exclude_set]
    for sample in samples:
        for ref in SLOT_RE.findall(sample):
            if ref not in slot_names:
                raise ValueError(
                    f"`mirror` of intent {intent_name!r} in {locale}: row "
                    f"{sample!r} inherited from {ref_locale} references "
                    f"{{{ref}}}, which this intent does not declare as a slot "
                    f"(declared: {sorted(slot_names)})"
                )
    if not samples:
        raise ValueError(
            f"`mirror` of intent {intent_name!r} in {locale} produces zero "
            f"samples (every {ref_locale} row is excluded)"
        )
    return samples


LEGACY_INTENT_SECTIONS = ("static_intents", "explicit_intents", "templates")
EXPLICIT_INTENT_KEYS = frozenset({"samples", "slots"})
TEMPLATE_INTENT_KEYS = frozenset({"templates", "slots"})


MODELS_DIR = Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
"""The interaction-model layout owner: templates/ and model_<locale>.json live here."""


def build_model(config: dict, locale: str | None = None,
                _mirror_stack: tuple[str, ...] = ()) -> dict:
    """Build the full interaction model from YAML config.

    `locale` is the building locale; it is only required when the template
    carries a `mirror` directive (JF-855), which needs it to resolve the
    reference template. `_mirror_stack` is internal cycle bookkeeping.
    """
    # A template declares its intents in exactly one of the two forms; a
    # mix would silently concatenate both lists with no ordering rule.
    used_legacy = [s for s in LEGACY_INTENT_SECTIONS if s in config]
    if "intents" in config and used_legacy:
        raise ValueError(
            "template declares both the ordered `intents` list and the "
            f"legacy grouped section(s) {used_legacy}; use one form only "
            "(see the module docstring)"
        )

    vocabulary = config.get("vocabulary", {})
    intents = []

    # Static intents (AMAZON.* built-ins with no samples)
    for name in config.get("static_intents", []):
        if not isinstance(name, str):
            raise ValueError(
                f"static_intents entries must be plain strings, got {name!r}"
            )
        intents.append({"name": name})

    # Explicit intents (with hardcoded samples)
    for name, intent_config in config.get("explicit_intents", {}).items():
        _reject_unknown_keys(intent_config, EXPLICIT_INTENT_KEYS, f"explicit_intents.{name}")
        intent = {"name": name}
        if "samples" in intent_config:
            intent["samples"] = intent_config["samples"]
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

    # Template-based intents
    for name, intent_config in config.get("templates", {}).items():
        _reject_unknown_keys(intent_config, TEMPLATE_INTENT_KEYS, f"templates.{name}")
        templates = intent_config.get("templates", [])
        slot_names = _slot_names_of(intent_config)
        samples = generate_samples(templates, vocabulary, slot_names)

        intent = {"name": name, "samples": samples}
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

    # Ordered intents (list form). Emitted after the legacy grouped
    # sections; a template uses either form, never both. See the module
    # docstring for the entry shapes.
    for item in config.get("intents", []):
        intents.append(
            build_ordered_intent(item, vocabulary, locale, _mirror_stack)
        )

    # Build language model
    language_model = {
        "invocationName": config.get("invocationName", "jelly fin"),
        "intents": intents,
    }

    # Add custom slot types
    types_config = config.get("types", {})
    if types_config:
        types_list = []
        for type_name, type_def in types_config.items():
            type_entry = {"name": type_name, "values": []}
            for v in type_def.get("values", []):
                type_entry["values"].append(build_type_value(v))
            types_list.append(type_entry)
        language_model["types"] = types_list

    # Optional modelConfiguration (e.g. fallbackIntentSensitivity).
    # Emitted after types, the position committed models use.
    model_configuration = config.get("modelConfiguration")
    if model_configuration:
        language_model["modelConfiguration"] = model_configuration

    result: dict = {"languageModel": language_model}

    # Pass through dialog section if defined in the YAML template.
    # Dialog.ElicitSlot requires the target intent to be listed in
    # dialog.intents — without it Alexa silently ignores the directive
    # and routes follow-up utterances through general NLU.
    dialog_config = config.get("dialog")
    if dialog_config:
        result["dialog"] = dialog_config

    # Pass through the prompts section if defined (elicitation prompt
    # variations referenced by dialog slots' prompts.elicitation ids).
    # Emitted after dialog, matching the committed models' section order.
    prompts_config = config.get("prompts")
    if prompts_config:
        result["prompts"] = prompts_config

    return result


def serialize_model(model: dict) -> str:
    """The single writer serialization: indent=2, ensure_ascii=False, trailing
    newline.

    This function IS the byte-identity contract of the golden masters. The
    regen-equality check in validate_interaction_models.py compares committed
    JSONs against this same function, so a future writer change updates both
    sides together instead of false-positive-ing every templated locale.
    """
    return json.dumps(model, indent=2, ensure_ascii=False) + "\n"


def main():
    locale = sys.argv[1] if len(sys.argv) > 1 else "it-IT"

    base_dir = Path(__file__).resolve().parent.parent / "Jellyfin.Plugin.AlexaSkill" / "Alexa" / "InteractionModel"
    template_path = base_dir / "templates" / f"{locale}.yaml"
    output_path = base_dir / f"model_{locale}.json"

    if not template_path.exists():
        print(f"Error: Template file not found: {template_path}", file=sys.stderr)
        sys.exit(1)

    with open(template_path) as f:
        config = yaml.safe_load(f)

    model = build_model(config, locale=locale)

    # Count samples for reporting
    total_samples = sum(
        len(i.get("samples", [])) for i in model["languageModel"]["intents"]
    )

    with open(output_path, "w") as f:
        f.write(serialize_model(model))

    print(f"Generated {output_path}")
    print(f"  {len(model['languageModel']['intents'])} intents, {total_samples} total samples")


if __name__ == "__main__":
    main()
