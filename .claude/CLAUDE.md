# Local Project Instructions (not checked in)

## Deployment

Deploy commands, server credentials, and simulator setup are in `.claude.local.md` (gitignored). Read that file before any deploy or E2E testing task.

## E2E Tests (SMAPI simulate-skill)

Skill ID and Jellyfin creds are in `.claude.local.md`. Key env vars:

```bash
export ASK_SKILL_ID="$ASK_SKILL_ID"   # see .claude.local.md
```

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
