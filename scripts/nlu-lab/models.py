"""Model sources for the lab: load an external model file, build from the
repo's YAML templates in memory, and shape the SMAPI deploy payload.

The committed model_*.json files are NEVER written here: the template path
runs the generator's build_model/serialize_model in memory only; only the
generator's own main() writes files, and nothing in this module calls it.
"""
import copy
import hashlib
import json
from pathlib import Path

import generate_interaction_model


def _unwrap_or_raise(model: dict, source: str) -> dict:
    if isinstance(model, dict) and "interactionModel" in model:
        return copy.deepcopy(model)
    if isinstance(model, dict) and "languageModel" in model:
        return {"interactionModel": copy.deepcopy(model)}
    raise ValueError(
        f"{source}: neither an interactionModel wrapper nor a languageModel "
        "dict; not a model the lab can deploy"
    )


def load_model_file(path) -> dict:
    """Load an external model file, normalizing to the SMAPI wrapper shape.

    Accepts both the {"interactionModel": ...} payload SMAPI consumes and a
    bare build_model dict ({"languageModel": ...}); the return is always
    the wrapper. Raises ValueError on anything else.
    """
    data = json.loads(Path(path).read_text(encoding="utf-8"))
    return _unwrap_or_raise(data, str(path))


def build_from_template(locale: str) -> dict:
    """Build the locale's interaction model IN MEMORY from its YAML template.

    Feeds the generator's build_model with load_template_config's parsed
    template (never main(), which writes files). serialize_model runs once
    on the result so a model the committed masters' serializer cannot
    render fails HERE, at build time, instead of at deploy time; the string
    is discarded. Returns the bare build_model dict ({"languageModel":
    ...}); prepare_payload normalizes it.
    """
    config = generate_interaction_model.load_template_config(locale)
    model = generate_interaction_model.build_model(config, locale=locale)
    generate_interaction_model.serialize_model(model)
    return model


def prepare_payload(model: dict, lab_invocation_name: str) -> tuple[dict, str, list]:
    """Shape the SMAPI deploy payload with the LAB invocation name.

    Accepts either shape (bare build_model dict or the interactionModel
    wrapper) and deep-copies before mutating, so the caller's dict keeps
    its own invocation name and nested intents. Returns
    (payload, sha256, intent_roster): the payload in the SMAPI wrapper
    shape with languageModel.invocationName set to the lab name (never the
    source's), the sha256 hex digest over the canonical serialized payload
    (json.dumps sort_keys=True, ensure_ascii=False, UTF-8), and one roster
    entry per intent in languageModel.intents order carrying its first
    sample (None when the intent has none).
    """
    payload = _unwrap_or_raise(model, "model argument")
    language_model = payload["interactionModel"]["languageModel"]
    language_model["invocationName"] = lab_invocation_name

    canonical = json.dumps(payload, sort_keys=True, ensure_ascii=False)
    sha256 = hashlib.sha256(canonical.encode("utf-8")).hexdigest()

    intent_roster = [
        {"intent": i["name"], "sample": i["samples"][0] if i.get("samples") else None}
        for i in language_model["intents"]
    ]
    return payload, sha256, intent_roster
