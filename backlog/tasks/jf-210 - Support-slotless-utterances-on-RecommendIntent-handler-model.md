---
id: JF-210
title: Support slotless utterances on RecommendIntent (handler + model)
status: Done
assignee: []
created_date: '2026-05-23 17:24'
updated_date: '2026-05-25 10:27'
labels:
  - enhancement
  - interaction-model
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem
RecommendIntent currently requires a `{media_type}` slot. Users naturally say things like:
- "cosa mi consigli" (what do you recommend?)
- "non so cosa ascoltare" (I don't know what to listen to)
- "consigliami qualcosa" (recommend me something)
- "cosa guardo stasera" (what should I watch tonight?)

None of these contain a media type slot. They need to be added as slotless utterances.

## What needs to change

### Handler: `RecommendIntentHandler.cs`
The handler must handle a missing/empty `media_type` slot gracefully:
- If no slot provided → default to a random recommendation across all media types, or ask a follow-up question like "vuoi un film o della musica?"
- Consider whether to pick a random media type, show a mix, or elicit the slot

### Interaction model: all 17 locales
Add slotless utterances per locale. Italian candidates:
- `cosa mi consigli`
- `consigliami qualcosa`
- `non so cosa ascoltare`
- `cosa guardo stasera`

English candidates:
- `what do you recommend`
- `recommend me something`
- `I don't know what to listen to`
- `what should I watch`

Similar for other locales.

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Handler gracefully handles missing `media_type` slot (no crash, returns a useful response)
- [x] #2 Slotless utterances added to all 17 locale models
- [x] #3 Unit tests cover the missing-slot path
- [x] #4 NLU test fixtures updated for affected locales
<!-- SECTION:DESCRIPTION:END -->

<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added 5 slotless utterances to RecommendIntent in it-IT model (only locale missing them — other 16 already had slotless samples). Added unit test verifying slotless path defaults to Audio+Movie. Consolidated duplicate test into one. Build 0 errors, 1875 tests pass, models valid.
<!-- SECTION:FINAL_SUMMARY:END -->
