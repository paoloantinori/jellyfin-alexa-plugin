---
id: JF-155
title: >-
  Bug: NullReferenceException in PlayArtistSongsIntentHandler.HandleFuzzyMiss
  (it-IT)
status: Done
assignee: []
created_date: '2026-05-15 13:54'
updated_date: '2026-05-16 08:42'
labels:
  - bug
  - nullref
  - it-IT
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Reproduction

**locale**: it-IT  
**ErrorRef**: 19f7c685 (corr), 1da878fa (ERR)  
**Intent**: `PlayArtistSongsIntentHandler`  
**Timestamp**: 2026-05-15 15:50:46

## Stack Trace

```
System.NullReferenceException: Object reference not set to an instance of an object.
   at Jellyfin.Plugin.AlexaSkill.Alexa.Handler.BaseHandler.HandleFuzzyMiss[T](String query, IReadOnlyList`1 candidates, Func`2 selector, Func`2 matchExtractor, String mediaType, String locale, Func`2 autoPlayFunc, User user)
   at Jellyfin.Plugin.AlexaSkill.Alexa.Handler.PlayArtistSongsIntentHandler.HandleAsync(Request request, Context context, User user, SessionInfo session, CancellationToken cancellationToken)
   at Jellyfin.Plugin.AlexaSkill.Alexa.Handler.BaseHandler.HandleRequestAsync(Request request, Context context, Session alexaSession, CancellationToken cancellationToken)
   at Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline.RequestPipeline.ExecuteAsync(BaseHandler handler, Request skillRequest, Context context, Session alexaSession, CancellationToken cancellationToken)
   at Jellyfin.Plugin.AlexaSkill.Controller.AlexaSkillController.HandleIntentRequest()
```

## Analysis

The NRE occurs in `BaseHandler.HandleFuzzyMiss<T>()` when called from `PlayArtistSongsIntentHandler.HandleAsync`. Likely causes:
- `candidates` list is null (query returned no results before fuzzy matching)
- `selector` or `matchExtractor` func receives a null item
- One of the method parameters (`mediaType`, `locale`, `user`) is null

## Investigation Steps

1. Read `BaseHandler.HandleFuzzyMiss` to identify which dereference causes the NRE
2. Read `PlayArtistSongsIntentHandler.HandleAsync` to see how it calls HandleFuzzyMiss
3. Add null guards for candidates and individual items before entering fuzzy match
4. Verify it-IT interaction model has valid samples for this intent
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
Fixed NullReferenceException in HandleFuzzyMiss by adding three null guards: (1) early return for null/empty candidates, (2) null check on bestWithScore.Item, (3) null-coalesce on matchExtractor return. Added 4 regression tests.
<!-- SECTION:FINAL_SUMMARY:END -->
