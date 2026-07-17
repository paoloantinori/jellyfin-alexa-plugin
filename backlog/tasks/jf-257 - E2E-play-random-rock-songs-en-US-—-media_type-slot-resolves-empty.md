---
id: JF-257
title: 'E2E: "play random rock songs" (en-US) — media_type slot resolves empty'
status: Done
assignee: []
created_date: '2026-06-05 16:26'
updated_date: '2026-06-05 17:08'
labels:
  - e2e
  - nlu
  - en-US
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Failing E2E test**: `e2e:en-US - play random rock songs`

**Error**: `Slot 'media_type' resolved empty for 'play random rock songs' (en-US)`

NLU matches PlayRandomIntent but doesn't fill the `media_type` slot. The utterance "play random rock songs" needs better samples or slot values for the en-US interaction model so that "rock songs" resolves the media_type slot.

**Fixture**: `tests/integration/fixtures/e2e_en-US.yaml`
**Root cause area**: en-US interaction model (`model_en-US.json`) — PlayRandomIntent samples / MEDIA_TYPE slot values
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
Fixed en-US PlayRandomIntent media_type slot resolution. Removed 6 static samples ("Play random songs", "Play random {genre} songs", etc.) that competed with slotted {media_type} variants and won NLU match without filling the slot. Added concrete disambiguation anchor "Play random rock {media_type}".
<!-- SECTION:FINAL_SUMMARY:END -->
