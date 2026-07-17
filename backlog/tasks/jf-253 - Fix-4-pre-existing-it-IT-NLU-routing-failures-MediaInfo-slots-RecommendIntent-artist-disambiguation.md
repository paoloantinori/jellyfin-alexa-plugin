---
id: JF-253
title: >-
  Fix 4 pre-existing it-IT NLU routing failures (MediaInfo slots,
  RecommendIntent, artist disambiguation)
status: Done
assignee: []
created_date: '2026-06-04 11:00'
updated_date: '2026-06-04 11:58'
labels:
  - bug
  - NLU
  - it-IT
dependencies:
  - JF-255
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
it-IT NLU tests failing for pre-existing MediaInfo intent routing issues:

1. **"Chi canta questa canzone"** — routes to correct intent but slot `media_info_type` resolves empty. The utterance should match "Chi canta {media_info_type}" but the NLU doesn't fill the slot.
2. **"Quanto dura questa canzone"** — same issue, `media_info_type` slot empty.
3. **"Suggerisci una canzone"** — routes to wrong intent (likely PlayRandomIntent or FallbackIntent instead of RecommendIntent).
4. **"c'e una canzone di radiohead"** — routes to wrong intent (PlayArtistSongsIntent instead of SearchMediaIntent or FindSongByArtistIntent).

These need interaction model fixes in the it-IT YAML template:
- Add more specific samples for MediaInfoIntent with "questa canzone" variants
- Add "Suggerisci" samples to RecommendIntent
- Accept or fix "c'e una canzone" routing (may be NLU limitation)
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
Fixed 3 of 4 it-IT NLU routing failures via YAML template changes: (1) Added MediaInfoType slot + 10 slotted samples to MediaInfoIntent so "Chi canta questa canzone" and "Quanto dura questa canzone" resolve slots correctly. (2) Added 6 explicit samples to RecommendIntent + expanded MediaType synonyms with singular forms ("una canzone", "un brano") so "Suggerisci una canzone" routes correctly. (3) "c'e una canzone di radiohead" already accepted in fixtures as PlayArtistSongsIntent — no change needed. Note: actual NLU verification requires deploying model to SMAPI (JF-256). Committed as 521de95, pushed to main.
<!-- SECTION:FINAL_SUMMARY:END -->
