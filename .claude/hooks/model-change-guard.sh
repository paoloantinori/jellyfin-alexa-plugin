#!/usr/bin/env bash
# PostToolUse hook: interaction-model change guard (2026-09-22, Paolo's directive
# after the fourth wrapper-form routing gap in two days). Any edit to a model
# template or a generated model runs the cheap structural validators here and
# surfaces the nlu-verify protocol. The live wrapper battery (profile-nlu) needs
# the ask CLI and the model DEPLOYED, so it stays a skill step, not this hook.
set -u

input=$(cat)
file=$(printf '%s' "$input" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tool_input",{}).get("file_path",""))' 2>/dev/null)

case "$file" in
  */InteractionModel/templates/*.yaml|*/InteractionModel/model_*.json) ;;
  *) exit 0 ;;
esac

cd "$(dirname "$0")/../.." || exit 0
checks=$(
  python3 scripts/validate_interaction_models.py 2>&1 | tail -2
  python3 scripts/validate_locales.py 2>&1 | tail -2
)

python3 - "$file" <<PY
import json, sys

checks = """$checks""".strip()
context = (
    "Interaction model changed: " + sys.argv[1] + "\n"
    + "Structural checks:\n" + checks + "\n\n"
    + "Follow the nlu-verify skill before committing or deploying: regenerate the "
      "affected locale (scripts/generate_interaction_model.py), deploy the model, then "
      "run scripts/nlu_wrapper_battery.py so one-shot wrapper phrases are verified "
      "against the SAVED model. A phrase landing on AMAZON.FallbackIntent on device is "
      "a template-twin bug (add the infinitive/wrapper twin), never user error, and "
      "never a hand-edit of the generated JSON."
)
print(json.dumps({
    "hookSpecificOutput": {
        "hookEventName": "PostToolUse",
        "additionalContext": context,
    }
}))
PY
