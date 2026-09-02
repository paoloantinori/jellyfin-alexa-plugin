---
id: JF-397
title: >-
  FallbackIntent kills mid-dialog sessions: unrecognized answer ends the
  conversation instead of re-prompting
status: Done
assignee: []
created_date: '2026-08-23 05:56'
updated_date: '2026-08-23 06:17'
labels:
  - ux
  - multi-turn
  - fallback
milestone: m-15
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
UX finding (2026-08-23 multi-turn audit). FallbackIntentHandler answers CouldNotUnderstand as a Tell: the session dies. Mid-dialog (especially FindSong, where the user loses the whole conversation and restarts from the artist prompt), an unrecognized utterance should instead repeat the current question (or at minimum offer the reprompt) instead of ending the session. The FindSong controller override already routes FallbackIntent into the FindSong dialog when FindSongSessionData is active (CanHandle claims AMAZON.FallbackIntent); the gap is the GENERIC disambiguation/pagination/resume flows, where FallbackIntentHandler runs with no state awareness.

Fix direction: make FallbackIntentHandler state-aware: if any conversational state key (disambig_matches, pagination_state, resume_state) is present, respond with the flow's reprompt (Ask) instead of Tell. Keep the bare no-state behavior unchanged.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed in this commit (TDD: 3 RED cases incl. no-state guard). FallbackIntentHandler gained an attrs-aware overload: resume (re-asks ResumePromptSsml with title via optional ILibraryManager) > pagination (ShowMorePrompt) > disambiguation (AskNextMatch at stored index). FindSong dialogs unaffected (controller override claims their fallbacks). Known residual: unsupported-built-in intents during a dialog still Tell and end the session (pre-existing branch, left unchanged); ja/hi/ar reprompts use the same localized strings so no gap.
<!-- SECTION:FINAL_SUMMARY:END -->
