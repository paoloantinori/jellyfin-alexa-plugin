---
id: JF-195
title: Add extended MediaInfo utterances to Hindi (hi-IN) interaction model
status: Done
assignee: []
created_date: '2026-05-21 07:43'
updated_date: '2026-05-21 08:13'
labels:
  - interaction-model
  - i18n
  - media-info
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add the 8 new MediaInfoType slot values (निर्देशक/director, कलाकार/cast, सीज़न/season, एपिसोड/episode, सीरीज़/series, लेखक/author, कथावाचक/narrator, रेटिंग/rating) with Hindi synonyms and English alternatives to `model_hi-IN.json`. Add ~16 concrete utterances. Verify locale strings exist in `hi-IN.json`.
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
Added 8 new MediaInfoType slot values (निर्देशक, कलाकार, सीज़न, एपिसोड, सीरीज़, लेखक, कथावाचक, रेटिंग) with Hindi synonyms and 16 concrete utterances to model_hi-IN.json. Locale strings replaced with proper Hindi translations.
<!-- SECTION:FINAL_SUMMARY:END -->
