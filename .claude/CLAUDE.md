# Local Project Instructions (not checked in)

## Deploy Checklist — ALWAYS follow `.claude.local.md`

That file has the full mandatory checklist. Key rules that keep getting violated:

1. **BACKUP config before EVERY deploy** — Jellyfin wipes plugin config (users, API keys, settings) when the DLL changes. Fetch config via API, save locally, verify users > 0. If 0, STOP.
2. **DISCOVER skill ID before E2E tests** — Run `ask smapi list-skills-for-vendor` to find the current Jellyfin skill. Update `~/.ask/ask_states.json`. The plugin creates new skills when config is wiped.
3. **ENABLE skill after deploy** — `ask smapi set-skill-enablement --skill-id <ID> --stage development`
4. **VERIFY config survived** after deploy. Restore from backup if lost.
5. **CLEANUP stale skills** — Multiple Jellyfin skills cause NLU competition. Delete old ones.
6. **Use `$JELLYFIN_URL`** (public URL) for E2E tests, NOT localhost (that's minix-only).

## E2E Tests (SMAPI simulate-skill)

Skill ID and Jellyfin creds are in `.claude.local.md` and env vars.

### Run all E2E tests
```bash
./scripts/run_e2e_tests.sh \
  --jellyfin-url "$JELLYFIN_URL" \
  --jellyfin-api-key "$JELLYFIN_API_KEY" \
  --jellyfin-user "$JELLYFIN_USER" -v
```

### Run only artist search E2E tests
```bash
./scripts/run_e2e_tests.sh -k "coughing or coughin or pink or led or beatles or xyzzyfoo or radiohead or gruppo or cantante" \
  --jellyfin-url "$JELLYFIN_URL" \
  --jellyfin-api-key "$JELLYFIN_API_KEY" \
  --jellyfin-user "$JELLYFIN_USER" -v
```

### Dry-run (validate fixtures without SMAPI)
```bash
./scripts/run_e2e_tests.sh --dry-run
```

### Notes
- Test IDs use the utterance text, so filter with `-k "keyword"` matches substrings in the utterance
- Use it-IT for reliable simulate-skill (en-US competes with built-in Amazon skills)
- SMAPI delay default is 1.5s between calls; increase with `SMAPI_DELAY=3` if rate-limited
- **NLU competition**: Utterances starting with "suona i radio*" route to PlayRadioIntent (not artist). Heavy misspellings ("pink floid") may fail to resolve to any intent at the NLU level. These are tested at unit level instead.
- Artist E2E matrix covers: exact match, ASR truncation, multi-word prefix ("led zep"), "The" prefix ("beatles"), not found ("xyzzyfoo"), disambiguation carriers ("band radiohead", "gruppo pink floyd", "cantante soul coughing")
- Deploy interaction model: `ask smapi set-interaction-model --skill-id $ASK_SKILL_ID --stage development --locale it-IT --interaction-model file:/tmp/payload.json`

## Simulator (Built-in Intent Tester)

For quick handler-level testing without SMAPI/NLU. Runs on minix localhost:
```bash
SSH_OPTS="-F /dev/null -o StrictHostKeyChecking=no -i ~/.ssh/id_rsa"
ssh $SSH_OPTS pantinor@minix "curl -sf -X POST 'http://localhost:8096/Plugins/AlexaSkill/Simulator/Intent' \
  -H 'X-Emby-Token: 69088d9a2bd74af5945b3d5683a087d3' \
  -H 'Content-Type: application/json' \
  -d '{\"intentName\":\"QueryArtistLibraryIntent\",\"slots\":{\"musician\":\"soul coughing\"},\"locale\":\"it-IT\"}'"
```
- Slot format: flat strings `"musician":"soul coughing"`, NOT objects `"musician":{"value":"..."}`
- LaunchRequest is NOT an intent — simulator only handles intent requests
