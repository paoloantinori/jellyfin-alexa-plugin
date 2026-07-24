---
id: JF-259
title: 'E2E: "suonare musica rilassante" (it-IT) — mood slot resolves empty'
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
**Failing E2E test**: `e2e:it-IT - suonare musica rilassante`

**Error**: `Slot 'mood' resolved empty for 'suonare musica rilassante' (it-IT)`

NLU matches PlayMoodIntent but doesn't fill the `mood` slot. "rilassante" should resolve to the mood slot value. Either the mood slot type is missing "rilassante" as a value/synonym, or the sample utterances don't bind the slot correctly for this phrasing.

**Fixture**: `tests/integration/fixtures/e2e_it-IT.yaml`
**Root cause area**: it-IT interaction model template (`templates/it-IT.yaml`) — PlayMoodIntent samples / MOOD slot type values
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
Fixed it-IT PlayMoodMusicIntent mood slot resolution. Added infinitive verb forms (Suonare, Ascoltare, Riprodurre, Mettere + Di-prefix variants) to PlayMoodMusicIntent samples, since the original only had imperative forms. Added concrete anchor "Suonare musica rilassante".
<!-- SECTION:FINAL_SUMMARY:END -->
