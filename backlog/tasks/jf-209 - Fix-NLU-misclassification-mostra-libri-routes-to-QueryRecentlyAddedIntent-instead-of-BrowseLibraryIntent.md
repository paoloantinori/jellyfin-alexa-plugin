---
id: JF-209
title: >-
  Fix NLU misclassification: "mostra libri" routes to QueryRecentlyAddedIntent
  instead of BrowseLibraryIntent
status: Done
assignee: []
created_date: '2026-05-23 11:02'
updated_date: '2026-05-25 10:10'
labels:
  - nlu
  - it-IT
  - interaction-model
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Alexa resolves "mostra libri" (show books) to QueryRecentlyAddedIntent instead of BrowseLibraryIntent with category "libri". The NLU misclassifies the utterance. Fix by adding explicit utterance samples to BrowseLibraryIntent in the it-IT interaction model, such as "mostra libri", "mostra i libri", "mostra audiolibri". Debug logs confirmed the handler works correctly — the issue is upstream at Alexa NLU intent resolution.
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
Added 45 concrete utterance samples to BrowseLibraryIntent in it-IT interaction model (mostra/sfoglia/elenca × 9 categories with/without articles). Fixed Italian grammar: "i artisti"→"gli artisti", "i albums"→"gli albums". Added 5 NLU test cases. Build passes, 1874 tests pass, models validated.
<!-- SECTION:FINAL_SUMMARY:END -->
