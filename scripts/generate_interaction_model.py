#!/usr/bin/env python3
"""Generate Alexa interaction model JSON from compact YAML templates.

Usage:
    python scripts/generate_interaction_model.py [locale]

    locale: The locale to generate (e.g. it-IT). Defaults to it-IT.

Reads:  Alexa/InteractionModel/templates/<locale>.yaml
Writes: Alexa/InteractionModel/model_<locale>.json

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


def generate_samples(templates: list[str], vocabulary: dict[str, list[str]]) -> list[str]:
    """Generate all sample utterances from a list of templates."""
    samples = []
    for template in templates:
        samples.extend(expand_template(template, vocabulary))
    return samples


def build_model(config: dict) -> dict:
    """Build the full interaction model from YAML config."""
    vocabulary = config.get("vocabulary", {})
    intents = []

    # Static intents (AMAZON.* built-ins with no samples)
    for name in config.get("static_intents", []):
        intents.append({"name": name})

    # Explicit intents (with hardcoded samples)
    for name, intent_config in config.get("explicit_intents", {}).items():
        intent = {"name": name}
        if "samples" in intent_config:
            intent["samples"] = intent_config["samples"]
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

    # Template-based intents
    for name, intent_config in config.get("templates", {}).items():
        templates = intent_config.get("templates", [])
        samples = generate_samples(templates, vocabulary)

        intent = {"name": name, "samples": samples}
        if "slots" in intent_config:
            intent["slots"] = intent_config["slots"]
        intents.append(intent)

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
            raw_values = type_def.get("values", [])
            for v in raw_values:
                if isinstance(v, str):
                    type_entry["values"].append({"name": {"value": v}})
                elif isinstance(v, dict):
                    entry = {"name": {"value": v["value"]}}
                    if "id" in v:
                        entry["id"] = v["id"]
                    if "synonyms" in v:
                        entry["name"]["synonyms"] = v["synonyms"]
                    type_entry["values"].append(entry)
            types_list.append(type_entry)
        language_model["types"] = types_list

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

    return {"languageModel": language_model}


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
        json.dump(model, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"Generated {output_path}")
    print(f"  {len(model['languageModel']['intents'])} intents, {total_samples} total samples")


if __name__ == "__main__":
    main()
