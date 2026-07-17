---
id: JF-194
title: Add extended MediaInfo utterances to Japanese (ja-JP) interaction model
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
Add the 8 new MediaInfoType slot values (監督, キャスト, シーズン, エピソード, シリーズ, 著者, ナレーター, 評価) with Japanese synonyms to `model_ja-JP.json`. Add ~16 concrete utterances. Verify locale strings exist in `ja-JP.json`.
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
Added 8 new MediaInfoType slot values (監督, キャスト, シーズン, エピソード, シリーズ, 著者, ナレーター, 評価) with Japanese synonyms and 16 concrete utterances to model_ja-JP.json. Locale strings replaced with proper Japanese translations.
<!-- SECTION:FINAL_SUMMARY:END -->
