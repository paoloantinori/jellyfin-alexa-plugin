---
id: JF-413
title: >-
  Audit ALL multi-step interactions for context loss (plain Ask without flow
  state; follow-ups falling through to general NLU) and convert to
  context-preserving mechanisms
status: To Do
assignee: []
created_date: '2026-08-28 18:37'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User-reported pattern (2026-08-28 20:23, live): PlayAlbumIntent's album elicit was a plain ResponseBuilder.Ask with NO flow state, so after "Quale album vuoi ascoltare?" the user's follow-up "quali ci sono" went through general NLU, routed to QueryRecentlyAddedIntent, and surfaced unrelated recently-added content: the conversational thread was lost. Fixed for the album elicit the same day by converting to Dialog.ElicitSlot (PlayAlbumIntent is registered in dialog.intents with elicitationRequired=false; the musician slot survives the round-trip). The user suspects the same defect class exists in OTHER multi-step interactions.

Audit scope: every handler returning Ask()/open-session prompts. Known flow-state mechanisms to compare against: FindSongSessionData + Dialog.ElicitSlot (the reference implementation, FindSongIntentHandler.BuildElicitSlotResponse), DisambiguationHelper state (disambig_type/matches/index via Yes/No intents), crossmedia_notfound_* attrs (JF-363), resume_state (LaunchRequest), pagination_state, ConversationalFlows namespacing + mutual exclusion (JF-398). A plain Ask whose follow-up relies on general NLU re-matching the original intent is the defect shape.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Inventory of every multi-step conversational flow: for each handler that returns Ask()/reprompt (disambiguation, cross-media suggestion, resume prompt, FindSong elicitation, album elicit, book disambiguation, pagination, queue listing, any ElicitSlot user), record: prompt source, session-state written (if any), follow-up routing mechanism (Dialog.ElicitSlot vs plain session + general NLU vs yes/no intents), and dialog.intents registration per locale
- [ ] #2 Verdict per flow: CONTEXT-PRESERVING vs CONTEXT-LOSING, with the concrete failure scenario for each losing one (the pattern to match: 2026-08-28 20:23, album elicit plain Ask followed by 'quali ci sono' routing to QueryRecentlyAdded and surfacing unrelated recent content)
- [ ] #3 Every context-losing flow either converted to Dialog.ElicitSlot (when a single slot should capture the answer and the intent is registered in dialog.intents in ALL 17 locales) or given explicit flow state consumed by the router/yes-no handlers (JF-398 namespacing), with unit tests per converted flow
- [ ] #4 Cross-check JF-401 asymmetry (dialog.intents registration differs across locales) since ElicitSlot silently fails where unregistered
- [ ] #5 On-device verification of at least the converted flows on it-IT
<!-- AC:END -->

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
