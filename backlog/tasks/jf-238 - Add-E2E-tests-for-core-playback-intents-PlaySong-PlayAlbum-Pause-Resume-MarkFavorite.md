---
id: JF-238
title: >-
  Add E2E tests for core playback intents (PlaySong, PlayAlbum, Pause, Resume,
  MarkFavorite)
status: Done
assignee: []
created_date: '2026-06-01 09:06'
updated_date: '2026-06-01 09:45'
labels:
  - testing
  - e2e
dependencies: []
references:
  - tests/integration/fixtures/e2e_it-IT.yaml
  - tests/integration/test_e2e.py
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Five core intents have zero E2E test coverage despite being among the most-used features. Add E2E test cases to `tests/integration/fixtures/e2e_it-IT.yaml` for: PlaySongIntent, PlayAlbumIntent, AMAZON.PauseIntent, AMAZON.ResumeIntent, MarkFavoriteIntent.

**Why**: These are the highest-risk intents for manual screenshot testing — they exercise the full SMAPI→skill endpoint→Jellyfin pipeline and have no end-to-end validation. Bugs here will be found manually or not at all.

**Implementation plan**:
1. Add 5 E2E test entries to `e2e_it-IT.yaml`:
   - `"suona la canzone bohemian rhapsody"` → PlaySongIntent, expected_slots: {song: {}}, expected_response_type: any
   - `"riproduci album thriller"` → PlayAlbumIntent, expected_slots: {album: {}}, expected_response_type: any
   - `"pausa"` → AMAZON.PauseIntent, expected_slots: {}, expected_response_type: any
   - `"riprendi"` → AMAZON.ResumeIntent, expected_slots: {}, expected_response_type: any (requires audio playing first — may need ordering constraint)
   - `"aggiungi ai preferiti"` → MarkFavoriteIntent, expected_slots: {}, expected_response_type: any
2. Run `./scripts/run_e2e_tests.sh --dry-run` to validate fixtures
3. Run live E2E: `./scripts/run_e2e_tests.sh --jellyfin-url $JELLYFIN_URL --jellyfin-api-key $JELLYFIN_API_KEY --jellyfin-user $JELLYFIN_USER -k "bohemian or thriller or pausa or riprendi or preferiti"`
4. Note: ResumeIntent E2E may be unreliable (needs audio playing). If so, mark with comment and rely on unit tests instead.

**Context**: E2E tests use SMAPI `simulate-skill` which sends real Alexa requests through the skill endpoint to Jellyfin. it-IT is preferred for simulate-skill (en-US competes with built-in Amazon skills). Each fixture entry needs: utterance, expected_intent, expected_slots, expected_response_type.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 e2e_it-IT.yaml has test entries for PlaySongIntent (suona la canzone), PlayAlbumIntent (riproduci album), AMAZON.PauseIntent (pausa), MarkFavoriteIntent (aggiungi ai preferiti)
- [ ] #2 AMAZON.ResumeIntent has an E2E entry OR is documented as unreliable for simulate-skill
- [ ] #3 dry-run passes with no fixture validation errors
- [ ] #4 Live E2E passes for all new test cases (or failures are documented as known SMAPI limitations)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added 5 E2E test fixtures to e2e_it-IT.yaml: PlaySongIntent (suona la canzone bohemian rhapsody), PlayAlbumIntent (riproduci album thriller), AMAZON.PauseIntent (pausa), MarkFavoriteIntent (aggiungi ai preferiti), AMAZON.ResumeIntent (riprendi with caveat about session continuity). Dry-run validates all 44 fixtures (39 existing + 5 new).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
