---
name: verify
description: Systematic post-deploy verification using the Jellyfin simulator and log monitoring. Use after deploying to minix, before committing. Defines a test matrix, fires simulator invocations, and checks podman logs. Triggers: "verify on local instance", "test on jellyfin", "verify the deploy", or as the mandatory step after /deploy.
---

# Post-Deploy Verification

After deploying, systematically verify all changed behaviors using the built-in simulator + podman logs. Do NOT commit until this protocol passes.

## Philosophy

Ad-hoc checks miss edge cases. This protocol forces you to define ALL test paths upfront, execute them in batch, and verify via logs that the correct code paths ran.

## Step 1: Define the Test Matrix

Before running anything, list ALL distinct paths for the change. Minimum 5 scenarios:

| # | Scenario | Expected Behavior | Expected Log Pattern |
|---|----------|-------------------|----------------------|
| 1 | Happy path | Correct result | Specific log trace |
| 2 | Zero results | MediaNotFound or graceful fallback | "no results" or "returned 0" |
| 3 | Boundary condition | Threshold behavior | Threshold-related log |
| 4 | Unrelated query | Existing behavior unchanged | No new code path triggered |
| 5 | Error/fallback path | Graceful degradation | Fallback or error log |

Write this table in the conversation BEFORE running tests.

## Step 2: Fire Simulator Invocations

Run ALL test cases in a single batch using the simulator:

```bash
API="http://minix:8096/Plugins/AlexaSkill/Simulator/Intent"
TOKEN="REDACTED"

# Test case N
curl -s -X POST "$API" \
  -H "X-Emby-Token: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"intentName":"SearchMediaIntent","slots":{"query":"test"},"locale":"en-US","sessionId":"tN"}' \
  | python3 -c "import sys,json; d=json.load(sys.stdin); r=d['response']; \
    print(f'EndSession: {r[\"shouldEndSession\"]}, HasAudio: {any(dd.get(\"type\")==\"AudioPlayer.Play\" for dd in r.get(\"directives\",[]))}, Disambig: {\"disambig_matches\" in d.get(\"sessionAttributes\",{})}')"
```

Available intent names: `SearchMediaIntent`, `PlaySongIntent`, `PlayAlbumIntent`, `PlayArtistSongsIntent`, `BrowseLibraryIntent`, `MediaInfoIntent`, `FavoriteToggleIntent`.

## Step 3: Check Logs

```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
ssh $SSH_OPTS pantinor@minix 'podman logs --tail 40 jellyfin 2>&1' | grep -E "pattern1|pattern2"
```

The log output MUST confirm:
- Each test case exercised the expected code path
- No unexpected errors or warnings appeared
- The fallback/happy path distinction is visible in log traces

## Step 4: Confirm or Fix

- **All paths correct** → proceed to commit
- **Any path wrong** → investigate, fix, redeploy, re-verify
- **Do NOT commit on partial verification** — all matrix rows must pass

## Simulator Details

- **Endpoint**: `POST http://minix:8096/Plugins/AlexaSkill/Simulator/Intent`
- **Auth**: `X-Emby-Token: REDACTED`
- **Body**: `{"intentName":"...", "slots":{"slotName":"value"}, "locale":"en-US", "sessionId":"unique"}`
- **Response**: Full SkillResponse JSON (directives, outputSpeech, sessionAttributes)
- **Logs**: `podman logs --tail N jellyfin` — log entries use the handler class name as category
