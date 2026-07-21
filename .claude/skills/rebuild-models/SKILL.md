---
name: rebuild-models
description: Push updated embedded interaction models to Alexa via the plugin's rebuild endpoint. Use after modifying interaction model JSONs (slot values, utterances, intents) without a full version bump. Triggers: "rebuild models", "push models", "update interaction models", "deploy models", "refresh models".
---

# Rebuild Interaction Models

Push all 17 embedded interaction models to Alexa via the plugin's own API.
This bypasses the version-check gate that normally only deploys models on startup when the plugin version changes.

## When to Use

- Added or changed slot values in `model_*.json` files (e.g. new BrowseCategory values)
- Added or modified utterances
- Added new intents
- Any change to `Alexa/InteractionModel/model_*.json` that needs to be live on Alexa

## Prerequisites

- The plugin must be running and a user must be configured with valid SMAPI credentials
- Interaction model JSONs in `Alexa/InteractionModel/` are already updated in the codebase
- If the DLL itself also changed, run the `deploy` skill first (it includes this step)

## Steps

### 1. Rebuild via Plugin API

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
USER_ID=$(ssh $SSH_OPTS pantinor@minix "curl -sf 'http://localhost:8096/Plugins/c5df7de087774b3ca70d5c3dae359c9e/Configuration' \
  -H 'X-Emby-Token: $JELLYFIN_API_KEY'" | python3 -c "import json,sys; print(json.load(sys.stdin)['Users'][0]['Id'])")
ssh $SSH_OPTS pantinor@minix "curl -s -X POST 'http://localhost:8096/alexaskill/api/custom-model/rebuild' \
  -H 'X-Emby-Token: $JELLYFIN_API_KEY' -H 'Content-Type: application/json' \
  -d '{\"userId\":\"$USER_ID\"}'"
```

### 2. Verify Response

The endpoint polls SMAPI until all locale model builds complete (up to ~2 minutes).
Must return:
```json
{
  "success": true,
  "message": "Rebuilt 17 models — 17 succeeded, 0 failed",
  "locales": {
    "ar-SA": { "success": true, "status": "SUCCEEDED", "error": null },
    "de-DE": { "success": true, "status": "SUCCEEDED", "error": null },
    ...
  }
}
```

If `success` is `false`, check the per-locale `error` fields for details.
If error `"User has no SMAPI device token"`: the user needs to re-authorize via the config UI.
If error `"Plugin manifest not loaded"`: restart Jellyfin first.

### 3. Verify with Simulator

Test that the new slot values are recognized:

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
ssh $SSH_OPTS pantinor@minix "curl -sf -X POST 'http://localhost:8096/Plugins/AlexaSkill/Simulator/Intent' \
  -H 'X-Emby-Token: $JELLYFIN_API_KEY' -H 'Content-Type: application/json' \
  -d '{\"intentName\":\"<IntentName>\",\"slots\":{\"<slot_name>\":\"<new_value>\"},\"locale\":\"it-IT\"}'"
```

Note: the simulator tests handler code directly and bypasses Alexa NLU,
so it works even before the SMAPI build completes.
Voice testing requires the build to finish.

## Key Facts

- Uses the plugin's `POST /alexaskill/api/custom-model/rebuild` endpoint — **never call SMAPI directly**
- The endpoint pushes all 17 locale models and **waits for SMAPI builds to complete** before returning
- Response includes per-locale SUCCEEDED/FAILED status — no separate polling step needed
- No version bump or restart required
- Also available as the "Rebuild All Models" button in the Jellyfin config UI
