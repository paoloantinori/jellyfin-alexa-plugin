---
id: JF-191
title: >-
  Add extended MediaInfo utterances to English locales (en-AU, en-CA, en-GB,
  en-IN)
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
Copy the en-US MediaInfoType slot values and utterances to `model_en-AU.json`, `model_en-CA.json`, `model_en-GB.json`, `model_en-IN.json`. These use identical English text. Add the 8 slot values (director, cast, season, episode, series, author, narrator, rating) with synonyms and ~16 concrete utterances per locale.
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
Copied en-US MediaInfoType slot values and 15 concrete utterances to model_en-AU.json, model_en-CA.json, model_en-GB.json, model_en-IN.json. All 4 locales now have 15 slot values (7 original + 8 new: director, cast, season, episode, series, author, narrator, rating). Locale strings were already present.
<!-- SECTION:FINAL_SUMMARY:END -->
