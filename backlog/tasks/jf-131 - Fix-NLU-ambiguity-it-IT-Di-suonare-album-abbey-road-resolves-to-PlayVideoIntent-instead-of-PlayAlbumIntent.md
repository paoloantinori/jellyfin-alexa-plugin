---
id: JF-131
title: >-
  Fix NLU ambiguity: it-IT "Di suonare album abbey road" resolves to
  PlayVideoIntent instead of PlayAlbumIntent
status: Done
assignee: []
created_date: '2026-05-12 09:41'
updated_date: '2026-05-12 11:33'
labels:
  - nlu
  - it-IT
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The utterance "Di suonare album abbey road" (it-IT) resolves to PlayVideoIntent instead of PlayAlbumIntent. This is an NLU disambiguation issue — Alexa's NLU picks the generic PlayVideoIntent over PlayAlbumIntent.

The interaction model likely needs more concrete (non-slotted) sample utterances for PlayAlbumIntent to improve disambiguation, or the PlayVideoIntent samples may be too greedy.

NLU test fixture: it-IT - "Di suonare album abbey road" (expected PlayAlbumIntent, got PlayVideoIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed by adding 46 concrete Italian sample utterances to PlayAlbumIntent with "album"/"disco" keyword anchoring and article variants. The root cause was PlayVideoIntent's greedy AMAZON.SearchQuery slot matching "abbey road" when PlayAlbumIntent's AlbumName slot didn't contain it. All 1019 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
