---
id: JF-306
title: >-
  FindSong NLU routing ambiguity — 'quali canzoni abbiamo' / 'trova una canzone
  chiamata X' misroute (FindSongIntent/BrowseLibrary vs FindSongByArtistIntent)
status: To Do
assignee: []
created_date: '2026-07-03 20:56'
updated_date: '2026-07-13 20:18'
labels:
  - bug
  - interaction-model
  - nlu
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10'
  - JF-303
  - JF-305
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SURFACED during the JF-305 v0.9.3.0 E2E run (not a JF-305 regression — JF-305 touched 0 FindSong lines). 2 it-IT simulate-skill E2E fixtures fail on NLU routing:

- 'quali canzoni abbiamo' -> expected FindSongByArtistIntent, got FindSongIntent (simulate-skill) / BrowseLibraryIntent (profile-nlu) — INCONSISTENT across engines.
- 'trova una canzone chiamata bohemian rhapsody' -> expected FindSongByArtistIntent, got FindSongIntent.

ROOT-CAUSE CONTEXT (from JF-303): FindSongIntent / FindSongByArtistIntent are NOT registered in any model's languageModel.intents[] (FindSong is a CODE-ONLY flow reached via AMAZON.FallbackIntent + Dialog.ElicitSlot; the intents exist only in dialog.intents). So NLU routing to a specific FindSong variant is inherently fragile — there are no samples for the model to match; it relies on the Fallback path, and the selected FindSong sub-intent is unstable/ambiguous.

CANDIDATE FIXES (investigate + pick one):
1. Tighten FindSong routing: add real samples for FindSongByArtistIntent (e.g. 'quali canzoni abbiamo di {musician}', 'trova una canzone chiamata {title}') to languageModel.intents[] across locales so the NLU has something concrete to match — but NOTE this may conflict with the code-only-via-Fallback design (see FindSongIntentHandler.CanHandle which accepts FallbackIntent with active session). Verify the handler still works if the intent gets direct samples.
2. Update the 2 fixtures to the actual routing (accept FindSongIntent instead of FindSongByArtistIntent) if the routing is deemed acceptable as-is.
3. Re-examine whether FindSongByArtistIntent should even be a distinct expected outcome for these utterances.

EVIDENCE: profile-nlu (it-IT) + simulate-skill give different selected intents for the same utterance — the signature of NLU ambiguity. git show f141540 (JF-305 model commit) touched 0 FindSong lines, confirming not a regression.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Root cause confirmed: is the misrouting because FindSong intents lack samples in languageModel.intents[] (code-only-via-Fallback design), or a different cause?
- [ ] #2 Decision recorded: tighten FindSong samples (and verify handler still works) vs update the 2 fixtures to actual routing vs accept-as-is
- [ ] #3 If tightening: FindSongByArtistIntent added to languageModel.intents[] with samples across locales, verified via profile-nlu that the 2 utterances route correctly, no regression to the Fallback-based FindSong multi-turn
- [ ] #4 The 2 it-IT E2E fixtures pass (or are updated to the agreed routing) and the full E2E suite is green
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
