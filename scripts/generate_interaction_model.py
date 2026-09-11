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

- Ordered `intents` list (used by en-US, the JF-316 migration form): each
  item is either a bare string (a name-only intent) or a mapping with
  `name` plus optional `samples` (verbatim list), `templates`
  (vocabulary-expanded; template strings without {vocab} refs pass through
  unchanged), and `slots`. The authored list order is preserved exactly,
  so static AMAZON.* intents can sit between custom intents the way the
  committed hand-grown models have them.

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


def build_ordered_intent(item, vocabulary: dict[str, list[str]]) -> dict:
    """Build one intent from an ordered `intents` list entry.

    A bare string becomes a name-only intent. A mapping requires `name`
    and may carry `samples` (emitted verbatim, in order), `templates`
    (vocabulary-expanded; expansions are appended after the verbatim
    samples), and `slots` (verbatim, key order preserved).

    As with type values, the emitted key order follows the YAML document
    order (committed hand-grown models mix `name, samples, slots` and
    `name, slots, samples`). The merged samples list (verbatim +
    template expansions) is emitted once, at the position of whichever
    of `samples`/`templates` appears first in the YAML.
    """
    if isinstance(item, str):
        return {"name": item}
    name = item.get("name")
    if not name:
        raise ValueError(f"ordered intents entry missing 'name': {item!r}")
    slot_names = frozenset(
        s["name"] for s in item.get("slots", []) if isinstance(s, dict)
    )
    samples = list(item.get("samples", []))
    if "templates" in item:
        samples.extend(generate_samples(item["templates"], vocabulary, slot_names))
    has_samples = "samples" in item or "templates" in item

    intent: dict = {}
    for key in item:
        if key == "name":
            intent["name"] = name
        elif key in ("samples", "templates"):
            if has_samples and "samples" not in intent:
                intent["samples"] = samples
        elif key == "slots":
            intent["slots"] = item["slots"]
        else:
            raise ValueError(
                f"unknown key {key!r} in ordered intent {name!r} "
                "(allowed: name, samples, templates, slots)"
            )
    return intent


LEGACY_INTENT_SECTIONS = ("static_intents", "explicit_intents", "templates")
EXPLICIT_INTENT_KEYS = frozenset({"samples", "slots"})
TEMPLATE_INTENT_KEYS = frozenset({"templates", "slots"})


def build_model(config: dict) -> dict:
    """Build the full interaction model from YAML config."""
    # A template declares its intents in exactly one of the two forms; a
    # mix would silently concatenate both lists with no ordering rule.
    used_legacy = [s for s in LEGACY_INTENT_SECTIONS if config.get(s)]
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
        unknown = set(intent_config) - EXPLICIT_INTENT_KEYS
        if unknown:
            raise ValueError(
                f"unknown key(s) {sorted(unknown)} in explicit_intents."
                f"{name} (allowed: {sorted(EXPLICIT_INTENT_KEYS)})"
            )
        intent = {"name": name}
        if "samples" in intent_config:
            intent["samples"] = intent_config["samples"]
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

    # Template-based intents
    for name, intent_config in config.get("templates", {}).items():
        unknown = set(intent_config) - TEMPLATE_INTENT_KEYS
        if unknown:
            raise ValueError(
                f"unknown key(s) {sorted(unknown)} in templates.{name} "
                f"(allowed: {sorted(TEMPLATE_INTENT_KEYS)})"
            )
        templates = intent_config.get("templates", [])
        slot_names = frozenset(
            s["name"] for s in intent_config.get("slots", []) if isinstance(s, dict)
        )
        samples = generate_samples(templates, vocabulary, slot_names)

        intent = {"name": name, "samples": samples}
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

    # Ordered intents (list form). Emitted after the legacy grouped
    # sections; a template uses either form, never both. See the module
    # docstring for the entry shapes.
    for item in config.get("intents", []):
        intents.append(build_ordered_intent(item, vocabulary))

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

    # Add slot samples to matching intent slots
    slot_samples = config.get("slot_samples", {})
    if slot_samples:
        for key, samples_list in slot_samples.items():
            intent_name, slot_name = key.split(".")
            for intent in intents:
                if intent.get("name") == intent_name and "slots" in intent:
                    for slot in intent["slots"]:
                        if slot.get("name") == slot_name:
                            slot["samples"] = samples_list

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

    model = build_model(config)

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
