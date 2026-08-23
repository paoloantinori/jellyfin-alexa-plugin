---
id: JF-395
title: >-
  FindSong disambiguation has no exit-by-no: user cannot say 'none of them',
  loops on FindSongInvalidPick forever
status: Done
assignee: []
created_date: '2026-08-23 05:56'
updated_date: '2026-08-23 06:10'
labels:
  - ux
  - multi-turn
  - findsong
milestone: m-15
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
UX dead-end (found by the 2026-08-23 multi-turn audit). In FindSongIntentHandler's Disambiguating state, ResolvePick only accepts a number (1-4), an ordinal word, or a partial title. A plain "no"/"nessuna"/"none of them" does NOT resolve: it loops back to the FindSongInvalidPick re-prompt indefinitely (bounded only by Alexa's session timeout). The user has no declared exit from the candidate picker.

Fix: accept negative answers in Disambiguating as a clean exit (Tell the not-found string and end the session, or offer a new keyword elicitation). Note the controller-level FindSong override currently hijacks even Yes/No intents while a FindSong dialog is open, so the fix belongs inside FindSongIntentHandler's own response handling (treat a negative utterance captured by GetAnySlotValue as an exit, or whitelist AMAZON.NoIntent into the dialog state machine).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in fd69f7f (TDD: 9 negative-answer cases across en/it/de/fr/es/pt + unparsable-non-negative guard, all RED-first). IsNegativeAnswer checked only after ResolvePick fails (so 'no, the second one' still picks). FindSongDisambigAbandoned string added to all 17 locales; validate_locales passes. NOTE: ja/hi/ar/nl negative words included in the word set but only smoke-tested via en/it/de/fr/es/pt cases; on-device verification for those locales still open.
<!-- SECTION:FINAL_SUMMARY:END -->
