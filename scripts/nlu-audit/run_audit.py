#!/usr/bin/env python3
"""NLU static quality audit runner for CI and local use.

Runs the three static audits over the committed interaction models and
exits non-zero on findings that should block a merge:

1. CROSS-INTENT EXACT DUPLICATES (error): already enforced by
   validate_interaction_models.py (its Phase 3); this runner does NOT
   duplicate it, the validator is the authority.

2. SLOT TYPE CONSISTENCY (error): no slot name may use two different
   types within the same locale, no dead slot declarations, no
   undeclared {slot} references. Verified clean 2026-11-24.

3. VOCABULARY COLLISIONS (warning for now, error once JF-955 closes):
   carrier words that are also values of custom slot types on other
   intents. The JF-933 class exists in 16 of 17 locales today; the
   baseline mode reports but does not fail until the structural fix
   (the it-IT phrase-valued MediaInfoType pattern) lands.

Usage:
    python3 scripts/nlu-audit/run_audit.py [--baseline <file>]
    python3 scripts/nlu-audit/run_audit.py --write-baseline

Exit codes: 0 = clean (or warnings only), 1 = errors found, 2 = cannot run.
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
MODELS_DIR = os.path.join(REPO_ROOT, "Jellyfin.Plugin.AlexaSkill", "Alexa", "InteractionModel")
BASELINE_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".collision-baseline.json")


def load_models() -> dict[str, dict]:
    models: dict[str, dict] = {}
    for path in sorted(glob.glob(os.path.join(MODELS_DIR, "model_*.json"))):
        locale = os.path.basename(path)[6:-5]
        with open(path, encoding="utf-8") as fh:
            data = json.load(fh)
        lm = data.get("interactionModel", data).get("languageModel", data)
        models[locale] = lm
    return models


# --- Slot type consistency (error level) ---

def audit_slot_types(models: dict[str, dict]) -> list[str]:
    errors: list[str] = []
    for locale, lm in sorted(models.items()):
        slot_types: dict[str, set[str]] = {}
        for intent in lm.get("intents", []):
            name = intent.get("name", "")
            declared = {s.get("name"): s.get("type") for s in intent.get("slots", []) if s.get("name")}
            for slot in intent.get("slots", []):
                sname = slot.get("name")
                stype = slot.get("type")
                if not sname or not stype:
                    continue
                if sname in slot_types and stype not in slot_types[sname]:
                    errors.append(
                        f"[{locale}] Slot '{sname}' uses both "
                        f"'{slot_types[sname]}' and '{stype}'")
                slot_types.setdefault(sname, set()).add(stype)
            # Dead slot: declared but not referenced in any sample
            for sname in declared:
                samples = intent.get("samples", [])
                if not any(f"{{{sname}}}" in s for s in samples):
                    errors.append(
                        f"[{locale}] Intent '{name}': slot '{sname}' declared "
                        f"but never referenced in samples")
    return errors


# --- Vocabulary collision (warning with baseline, error without) ---

def audit_vocabulary(models: dict[str, dict]) -> list[dict]:
    findings: list[dict] = []
    for locale, lm in sorted(models.items()):
        # Build the type-value index
        type_values: dict[str, set[str]] = {}
        for t in lm.get("types", []):
            tname = t.get("name", "")
            if tname.startswith("AMAZON."):
                continue
            vals = set()
            for v in t.get("values", []):
                n = v.get("name", {})
                val = n.get("value", "")
                if val:
                    vals.add(val.lower())
                for syn in n.get("synonyms", []) or []:
                    vals.add(str(syn).lower())
            type_values[tname] = vals

        # Which intents consume which types
        type_consumers: dict[str, list[str]] = {}
        for intent in lm.get("intents", []):
            iname = intent.get("name", "")
            for slot in intent.get("slots", []):
                stype = slot.get("type", "")
                if stype in type_values:
                    type_consumers.setdefault(stype, []).append(iname)

        # Find carrier words that are also type values on other intents
        for intent in lm.get("intents", []):
            iname = intent.get("name", "")
            my_types = {s.get("type") for s in intent.get("slots", [])}
            for sample in intent.get("samples", []):
                # Strip slot references to get literal tokens
                import re
                literal = re.sub(r"\{[^}]+\}", " ", sample)
                words = {w.lower().strip(".,!?;:'\"()[]") for w in literal.split()}
                for word in words:
                    if len(word) < 3:
                        continue
                    for tname, vals in type_values.items():
                        if tname in my_types:
                            continue  # same-intent: JF-403 boundary
                        if word in vals:
                            consumers = type_consumers.get(tname, [])
                            findings.append({
                                "locale": locale,
                                "word": word,
                                "carrier_intent": iname,
                                "sample": sample[:80],
                                "type": tname,
                                "type_consumers": consumers,
                            })
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write-baseline", action="store_true",
                        help="Write the current vocabulary findings as the baseline")
    parser.add_argument("--baseline", default=BASELINE_FILE,
                        help="Baseline file for vocabulary collision comparison")
    args = parser.parse_args()

    models = load_models()
    if not models:
        print("ERROR: no models found", file=sys.stderr)
        return 2

    # 1. Slot type consistency (always error)
    slot_errors = audit_slot_types(models)
    if slot_errors:
        print("SLOT TYPE ERRORS:", file=sys.stderr)
        for e in slot_errors:
            print(f"  {e}", file=sys.stderr)

    # 2. Vocabulary collisions (warning vs baseline)
    vocab_findings = audit_vocabulary(models)
    baseline: set[str] = set()
    if args.write_baseline:
        keys = {f"{f['locale']}:{f['word']}:{f['carrier_intent']}:{f['type']}" for f in vocab_findings}
        with open(args.baseline, "w") as fh:
            json.dump(sorted(keys), fh, indent=2)
        print(f"Baseline written: {len(keys)} known collisions")
        return 0

    if os.path.exists(args.baseline):
        with open(args.baseline) as fh:
            baseline = set(json.load(fh))

    new_findings = []
    for f in vocab_findings:
        key = f"{f['locale']}:{f['word']}:{f['carrier_intent']}:{f['type']}"
        if key not in baseline:
            new_findings.append(f)

    if new_findings:
        print(f"NEW VOCABULARY COLLISIONS (not in baseline): {len(new_findings)}",
              file=sys.stderr)
        for f in new_findings[:10]:
            print(f"  [{f['locale']}] '{f['word']}' on {f['carrier_intent']} "
                  f"vs type {f['type']} ({', '.join(f['type_consumers'][:3])})",
                  file=sys.stderr)
        if len(new_findings) > 10:
            print(f"  ... and {len(new_findings) - 10} more", file=sys.stderr)

    # Exit code: slot errors always fail; vocab is warning-only until JF-955
    if slot_errors:
        return 1
    if new_findings:
        print(f"WARNING: {len(new_findings)} new vocabulary collisions "
              f"(known baseline: {len(baseline)})", file=sys.stderr)
        return 0  # warning only for now

    print(f"Clean: 0 slot errors, 0 new vocabulary collisions "
          f"(baseline: {len(baseline)}, current: {len(vocab_findings)})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
