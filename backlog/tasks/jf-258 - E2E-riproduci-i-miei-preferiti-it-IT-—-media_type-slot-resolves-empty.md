---
id: JF-258
title: 'E2E: "riproduci i miei preferiti" (it-IT) — media_type slot resolves empty'
status: Done
assignee: []
created_date: '2026-06-05 16:26'
updated_date: '2026-06-05 17:08'
labels:
  - e2e
  - nlu
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Failing E2E test**: `e2e:it-IT - riproduci i miei preferiti` (2 fixtures: riproduci i miei preferiti0, riproduci i miei preferiti1)

**Error**: `Slot 'media_type' resolved empty for 'riproduci i miei preferiti' (it-IT)`

NLU matches PlayFavoritesIntent but doesn't fill the `media_type` slot. The utterance "riproduci i miei preferiti" needs samples that teach Alexa to extract media_type, or the slot should be made optional in this intent.

**Fixture**: `tests/integration/fixtures/e2e_it-IT.yaml`
**Root cause area**: it-IT interaction model template (`templates/it-IT.yaml`) — PlayFavoritesIntent samples / MEDIA_TYPE slot
<!-- SECTION:DESCRIPTION:END -->

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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed it-IT PlayFavoritesIntent media_type slot resolution. Added templates "{imperative} i miei {media_type} preferiti" for when users specify a media type. Updated E2E fixture to remove media_type expectation for bare "riproduci i miei preferiti" since the handler already defaults to all media when media_type is null.
<!-- SECTION:FINAL_SUMMARY:END -->
