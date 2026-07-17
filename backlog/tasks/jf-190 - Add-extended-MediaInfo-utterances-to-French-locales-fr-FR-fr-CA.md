---
id: JF-190
title: 'Add extended MediaInfo utterances to French locales (fr-FR, fr-CA)'
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
Add the 8 new MediaInfoType slot values (réalisateur, distribution/casting, saison, épisode, série, auteur, narrateur, note) with French synonyms to `model_fr-FR.json` and `model_fr-CA.json`. Add ~16 concrete utterances per locale. Verify locale strings exist in each `fr-*.json`.
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
Added 8 new MediaInfoType slot values (réalisateur, distribution, saison, épisode, série, auteur, narrateur, note) with French synonyms and 16 concrete utterances to model_fr-FR.json and model_fr-CA.json. Replaced English placeholder locale strings with proper French translations in both locale files.
<!-- SECTION:FINAL_SUMMARY:END -->
