---
id: JF-407
title: >-
  Consolidation follow-ups from /simplify: ResolveActiveFlow arbitration, shared
  picker pick-words, AskLocalized helper
status: In Progress
assignee:
  - zai
created_date: '2026-08-23 11:59'
updated_date: '2026-08-29 14:44'
labels:
  - refactor
  - multi-turn
  - tech-debt
milestone: m-15
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-ups from the /simplify pass over JF-394..398 (commit 114b490 skipped these as beyond-cleanup scope):

1. ResolveActiveFlow: the resume > pagination > disambiguation arbitration is written three times (YesIntentHandler, NoIntentHandler, FallbackIntentHandler), each with its own comment. Move to ConversationalFlows.ResolveActiveFlow(sessionAttributes) -> Flow enum owning the order once; also folds the FindSong CanHandle-level FallbackIntent capture into the same mechanism. Add a Flow-typed MarkOthersInactive overload so call sites cannot pass a typo'd key array.
2. Shared pick-words: CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/ResolvePick/IsNegativeAnswer live private in FindSongIntentHandler, but numbered-candidate picking is a general DisambiguationHelper capability (used by 10 handlers). Moving them down gives every picker the cardinal/ordinal answer and the JF-395 negative-exit; today only FindSong has them.
3. AskLocalized helper: the SSML-or-plain Ask pattern (GetSsml ?? AskSsml : ResponseBuilder.Ask) is hand-written at ~6 sites (LaunchRequestHandler resume offer, FallbackIntentHandler re-ask, DisambiguationHelper x3, BaseHandler x2); one BaseHandler.AskLocalized(ssmlKey, textKey, repromptKey, locale, args) removes the drift the FallbackIntentHandler reprompt inconsistency came from.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Item-by-item (bounded, no behavior change):
1. AskLocalized helper in BaseHandler: one method for the SSML-or-plain Ask pattern (GetSsml ?? AskSsml : ResponseBuilder.Ask), replacing the ~6 hand-written sites (LaunchRequestHandler resume, FallbackIntentHandler re-ask, DisambiguationHelper x3, BaseHandler x2).
2. ResolveActiveFlow in ConversationalFlows: move the resume > pagination > disambiguation arbitration (currently duplicated in YesIntentHandler, NoIntentHandler, FallbackIntentHandler) to a single Flow-enum method.
3. Shared pick-words to DisambiguationHelper: CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/ResolvePick/IsNegativeAnswer currently private in FindSongIntentHandler; move to DisambiguationHelper so every picker gets cardinal/ordinal + JF-395 negative-exit.
Each item: tests stay green (no behavior change), commit individually.
<!-- SECTION:PLAN:END -->

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
