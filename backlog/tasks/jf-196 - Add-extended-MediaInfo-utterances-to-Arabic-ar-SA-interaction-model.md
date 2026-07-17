---
id: JF-196
title: Add extended MediaInfo utterances to Arabic (ar-SA) interaction model
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
Add the 8 new MediaInfoType slot values (مخرج/director, طاقم/cast, موسم/season, حلقة/episode, مسلسل/series, مؤلف/author, راوي/narrator, تقييم/rating) with Arabic synonyms to `model_ar-SA.json`. Add ~16 concrete utterances. Verify locale strings exist in `ar-SA.json`.
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
Added 8 new MediaInfoType slot values (مخرج, طاقم, موسم, حلقة, مسلسل, مؤلف, راوي, تقييم) with Arabic synonyms and 16 concrete utterances to model_ar-SA.json. Locale strings replaced with proper Arabic translations.
<!-- SECTION:FINAL_SUMMARY:END -->
