---
id: JF-414
title: >-
  Multilingual roll-out of the conversational model forms and context-preserving
  flows (indefinite album-by-artist samples, ElicitSlot support, dialog.intents
  parity across all 17 locales)
status: In Progress
assignee:
  - zai
created_date: '2026-08-28 18:37'
updated_date: '2026-08-29 06:36'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Several conversational-model fixes landed it-IT only (2026-08-28): the 44+10 indefinite album-by-artist PlayAlbumIntent samples ('un disco/un album di/dei {musician}', vorrei forms, carrier-anchored forms) and the ElicitSlot album elicit. The indefinite album-by-artist form was verified missing in ALL locales (en-US only has title+artist forms like 'play the album {album} by {musician}'), so every non-it-IT locale still routes 'an album by X' requests to Fallback/other skills. The context-loss audit task (separate) will produce more flows whose model-side support must be per-locale too.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Per-locale sample sets for the indefinite album-by-artist forms and any other it-IT-only conversational forms introduced by the context-loss fixes (en: 'an album by {musician}', de: 'ein album von {musician}', fr: 'un album de {musician}', es/pt/ja/hi equivalents; exact natural phrasing per locale reviewed, not machine-literal)
- [ ] #2 dialog.intents registration parity across all 17 locales for every intent that emits Dialog.ElicitSlot (resolves the JF-401 asymmetry: 6 intents registered in only 6 locales)
- [ ] #3 Handler-side flows are locale-agnostic (verify no it-IT hardcoded strings bypass ResponseStrings); new prompt strings added to all 17 locale files
- [ ] #4 NLU fixtures for the new forms in at least the locales with existing fixture coverage (per JF-400 extension order: pt-BR, ja, hi, en variants first)
- [ ] #5 Validators green (interaction models, locales, versions); NLU runs green for covered locales; on-device spot check on it-IT plus one more locale
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Phase A (models): add indefinite album-by-artist samples to PlayAlbumIntent in the 16 non-it-IT models, phrasing grounded in each locale's EXISTING imperative vocabulary (inspect current samples first, no machine-literal translations); verify musician slot type per locale before adding. Phase B (dialog parity decision): inspect the 6 locales' PlayEpisode prompt structure and decide model-delegated-everywhere vs manual-everywhere; minimal-risk path preferred. Phase C: validators + NLU fixtures for locales with existing fixture coverage (en-US first). Phase D: SMAPI push of all changed models (sequential set-interaction-model + status poll, background). Phase E: suite + commit + notes.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PUSH RESULTS (round 1): 10 locales SUCCEEDED (en-GB/AU/CA/IN, de, fr x2, es x3); en-US FAILED (dialog MismatchedSlotType - my dialog-flag script had hardcoded it-IT's AlbumName while those locales' languageModel uses AMAZON.MusicRecording); 5 recorded '?' (pt-BR/nl/ja/hi/ar - same mismatch or vendor-inactive, retried after fix). FIX: dialog slot types now aligned to each locale's languageModel types programmatically (no per-locale hardcoding left); validators PASS; re-push of the 6 in flight.

ROUTING VERIFIED on pushed models: de-DE 'ein Album von Queen' -> PlayAlbumIntent(musician=queen) PERFECT; fr-FR 'un album de coldplay' -> PlayAlbumIntent(musician=coldplay) PERFECT; en-GB 'an album by queen' -> PlayAlbumIntent (correct intent) BUT see the platform finding.

PLATFORM FINDING (en-*, probe-evidenced 2026-08-29): the AMAZON.Musician built-in REWRITES the raw slot value to a knowledge-graph canonical entity under en locales: queen->'Paula Abdul', the beatles->'John Lennon', coldplay->'Christopher Anthony John Martin', pink floyd->'Syd Barrett'. PRE-EXISTING behavior, NOT caused by the new samples (the pre-existing QueryArtistLibrary 'albums by {musician}' forms rewrite identically); it-IT and fr-FR do NOT rewrite (raw preserved). Handler consequence: artist search on the mangled full-person-name -> clean not-found ('Sorry, I couldn't find any albums with the artist Christopher Anthony John Martin', simulator-verified; no wrong plays - the not-found-first design holds). MITIGATION DIRECTION (not tonight): swap the musician slot to the catalog-backed JellyfinArtist custom type in the affected locales - that is the DESIGNED artist architecture (JF-96.2 catalog sync with phonetic synonyms) and custom types return raw spoken text, but slot-type consistency requires swapping it across all intents of those locales, a scoped decision (note: anti-pattern #10 was about ALBUM swaps; musician-to-catalog is the architecture's own pattern). Recorded as the JF-414 residual.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
