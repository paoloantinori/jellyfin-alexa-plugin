#!/usr/bin/env bash
# Validate an Alexa interaction model via SMAPI.
# Usage: ./scripts/validate_model.sh <locale> [model-file]
# Example: ./scripts/validate_model.sh it-IT

set -euo pipefail

LOCALE="${1:-}"
MODEL_FILE="${2:-}"
SKILL_ID="${3:-}"

if [ -z "$LOCALE" ]; then
  echo "Usage: $0 <locale> [model-file] [skill-id]"
  echo "  locale:     e.g. it-IT, en-US, de-DE"
  echo "  model-file: optional, defaults to Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_<locale>.json"
  echo "  skill-id:   optional, auto-detected from ~/.ask/ if omitted"
  exit 1
fi

if [ -z "$MODEL_FILE" ]; then
  MODEL_FILE="Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_${LOCALE}.json"
fi

if [ ! -f "$MODEL_FILE" ]; then
  echo "Model file not found: $MODEL_FILE"
  exit 1
fi

# Auto-detect skill ID if not provided
if [ -z "$SKILL_ID" ]; then
  ASK_CONFIG="${HOME}/.ask/ask_states.json"
  if [ -f "$ASK_CONFIG" ]; then
    SKILL_ID=$(python3 -c "
import json
with open('$ASK_CONFIG') as f:
    data = json.load(f)
for v in data.get('skillMetadata', {}).values():
    s = v.get('skill_id', '')
    if s:
        print(s)
        break
" 2>/dev/null || true)
  fi
fi

if [ -z "$SKILL_ID" ]; then
  echo "Could not find skill ID."
  echo "Usage: $0 <locale> [model-file] [skill-id]"
  exit 1
fi

# Extract access token from ask-cli config
TOKEN=""
ASK_CLI_CONFIG="${HOME}/.ask/cli_config"
if [ -f "$ASK_CLI_CONFIG" ]; then
  TOKEN=$(python3 -c "
import json
with open('$ASK_CLI_CONFIG') as f:
    data = json.load(f)
profiles = data.get('profiles', {})
# Try default profile first, then any other
for name in (['default'] + [k for k in profiles if k != 'default']):
    p = profiles.get(name, {})
    t = p.get('token', {})
    at = t.get('access_token', '')
    if at:
        print(at)
        break
" 2>/dev/null || true)
fi

if [ -z "$TOKEN" ]; then
  echo "Could not extract LWA access token from ${ASK_CLI_CONFIG}"
  echo "Run 'ask smapi get-vendor-list' first to authenticate."
  exit 1
fi

echo "Validating model: $MODEL_FILE"
echo "Locale: $LOCALE"
echo "Skill ID: $SKILL_ID"
echo ""

# SMAPI expects: {"interactionModel": {"languageModel": {...}}}
PAYLOAD=$(python3 -c "
import json, sys
with open('${MODEL_FILE}') as f:
    data = json.load(f)
if 'interactionModel' not in data:
    data = {'interactionModel': data}
json.dump(data, sys.stdout)
")

# Try to set the interaction model and capture the full response
RESPONSE=$(curl -s -w "\n%{http_code}" -X PUT \
  "https://api.amazonalexa.com/v1/skills/${SKILL_ID}/stages/development/interactionModel/locales/${LOCALE}" \
  -H "Authorization: Bearer ${TOKEN}" \
  -H "Content-Type: application/json" \
  -d "$PAYLOAD")

HTTP_CODE=$(echo "$RESPONSE" | tail -1)
BODY=$(echo "$RESPONSE" | sed '$d')

if [ "$HTTP_CODE" = "202" ] || [ "$HTTP_CODE" = "204" ]; then
  echo "Model accepted! (HTTP $HTTP_CODE)"
  echo "Building on Amazon's servers..."
else
  echo "Validation failed (HTTP $HTTP_CODE):"
  echo "$BODY" | python3 -m json.tool 2>/dev/null || echo "$BODY"
fi
