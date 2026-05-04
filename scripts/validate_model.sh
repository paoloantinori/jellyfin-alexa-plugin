#!/usr/bin/env bash
# Validate an Alexa interaction model via SMAPI.
# Usage: ./scripts/validate_model.sh <locale> [model-file] [skill-id]
# Example: ./scripts/validate_model.sh it-IT
#
# After uploading, waits for the build to complete and reports results.
# To check build status of a previously uploaded model without re-uploading:
#   ./scripts/validate_model.sh --status [locale] [skill-id]

set -euo pipefail

MODE="upload"
if [ "${1:-}" = "--status" ]; then
  MODE="status"
  shift
fi

LOCALE="${1:-}"
MODEL_FILE="${2:-}"
SKILL_ID="${3:-}"

if [ -z "$LOCALE" ]; then
  echo "Usage: $0 [--status] <locale> [model-file] [skill-id]"
  echo "  --status:   only check build status, don't upload"
  echo "  locale:     e.g. it-IT, en-US, de-DE, or 'all'"
  echo "  model-file: optional, defaults to Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_<locale>.json"
  echo "  skill-id:   optional, auto-detected from ~/.ask/ if omitted"
  exit 1
fi

# Handle 'all' locale
if [ "$LOCALE" = "all" ]; then
  LOCALES="de-DE en-AU en-CA en-GB en-IN en-US es-ES es-MX es-US fr-CA fr-FR it-IT"
  for L in $LOCALES; do
    echo "=== $L ==="
    "$0" $([ "$MODE" = "status" ] && echo "--status") "$L" "$MODEL_FILE" "$SKILL_ID" 2>&1 | grep -E "accepted|failed|SUCCEEDED|FAILED|error|Error|Build"
    echo ""
  done
  exit 0
fi

if [ "$MODE" = "upload" ]; then
  if [ -z "$MODEL_FILE" ]; then
    MODEL_FILE="Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_${LOCALE}.json"
  fi

  if [ ! -f "$MODEL_FILE" ]; then
    echo "Model file not found: $MODEL_FILE"
    exit 1
  fi
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
  echo "Usage: $0 [--status] <locale> [model-file] [skill-id]"
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

# Upload model (unless --status mode)
if [ "$MODE" = "upload" ]; then
  echo "Validating model: $MODEL_FILE"
  echo "Locale: $LOCALE"
  echo "Skill ID: $SKILL_ID"
  echo ""

  # SMAPI expects: {"interactionModel": {"languageModel": {...}}}
  PAYLOAD_FILE=$(mktemp)
  trap "rm -f ${PAYLOAD_FILE}" EXIT
  python3 -c "
import json, sys
with open('${MODEL_FILE}') as f:
    data = json.load(f)
if 'interactionModel' not in data:
    data = {'interactionModel': data}
with open('${PAYLOAD_FILE}', 'w') as out:
    json.dump(data, out, separators=(',', ':'))
"

  RESPONSE=$(curl -s -w "\n%{http_code}" -X PUT \
    "https://api.amazonalexa.com/v1/skills/${SKILL_ID}/stages/development/interactionModel/locales/${LOCALE}" \
    -H "Authorization: Bearer ${TOKEN}" \
    -H "Content-Type: application/json" \
    -d @"${PAYLOAD_FILE}")

  HTTP_CODE=$(echo "$RESPONSE" | tail -1)
  BODY=$(echo "$RESPONSE" | sed '$d')

  if [ "$HTTP_CODE" = "202" ] || [ "$HTTP_CODE" = "204" ]; then
    echo "Model accepted! (HTTP $HTTP_CODE)"
    echo "Waiting for build..."
  else
    echo "Validation failed (HTTP $HTTP_CODE):"
    echo "$BODY" | python3 -m json.tool 2>/dev/null || echo "$BODY"
    exit 1
  fi
else
  echo "Checking build status for: $LOCALE"
  echo "Skill ID: $SKILL_ID"
  echo ""
fi

# Poll build status until complete using get-skill-status endpoint
MAX_ATTEMPTS=30
ATTEMPT=0
while [ $ATTEMPT -lt $MAX_ATTEMPTS ]; do
  sleep 5
  ATTEMPT=$((ATTEMPT + 1))

  STATUS_RESPONSE=$(curl -s \
    "https://api.amazonalexa.com/v1/skills/${SKILL_ID}/status" \
    -H "Authorization: Bearer ${TOKEN}")

  BUILD_STATUS=$(echo "$STATUS_RESPONSE" | python3 -c "
import json, sys
data = json.load(sys.stdin)
locale_data = data.get('interactionModel', {}).get('${LOCALE}', {}).get('lastUpdateRequest', {})
status = locale_data.get('status', 'UNKNOWN')
errors = locale_data.get('errors', [])
if errors:
    msgs = [e.get('message', str(e)) for e in errors]
    print(f'FAILED: ' + '; '.join(msgs))
else:
    print(status)
" 2>/dev/null || echo "POLL_ERROR")

  if echo "$BUILD_STATUS" | grep -q "^FAILED"; then
    echo "Build failed: $BUILD_STATUS"
    exit 1
  elif [ "$BUILD_STATUS" = "SUCCEEDED" ]; then
    echo "Build SUCCEEDED for $LOCALE"
    exit 0
  elif echo "$BUILD_STATUS" | grep -q "IN_PROGRESS\|PENDING"; then
    echo "  [$ATTEMPT/$MAX_ATTEMPTS] Build in progress..."
  else
    echo "  [$ATTEMPT/$MAX_ATTEMPTS] Status: $BUILD_STATUS"
  fi
done

echo "Timed out waiting for build after $MAX_ATTEMPTS attempts."
echo "Check manually: ask smapi get-skill-status --skill-id $SKILL_ID"
exit 1
