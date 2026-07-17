---
id: JF-243
title: 'PostPlayBehavior: Yes/No intent handler integration'
status: Done
assignee: []
created_date: '2026-06-02 12:05'
updated_date: '2026-06-02 14:08'
labels:
  - feature
  - tdd
dependencies:
  - JF-241
  - JF-242
references:
  - /home/pantinor/.cc-mirror/zai/config/plans/cozy-sleeping-wreath.md
documentation:
  - Alexa/Handler/Intent/YesIntentHandler.cs
  - Alexa/Handler/Intent/NoIntentHandler.cs
  - Alexa/PostPlayState.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wire up YesIntentHandler and NoIntentHandler to handle the Ask mode response. **Write tests first (TDD).**

### YesIntentHandler changes:
Add PostPlayState check as **HIGHEST priority** (before resume/pagination/disambiguation checks) in the `HandleAsync` overload with `sessionAttributes` (line ~86).

Why highest priority: The PlaybackFinished event starts a NEW session (empty attributes), so no resume/pagination/disambiguation state will exist. PostPlayState is server-side.

```csharp
// Check for pending post-play ask (highest priority)
if (PostPlayState.TryGet(session.UserId, context.System.Device.DeviceID, out var state)
    && state.Mode == PostPlayBehavior.Ask)
{
    PostPlayState.Remove(session.UserId, context.System.Device.DeviceID);
    return HandlePostPlayYes(state.ItemId, user, session, context, locale, cancellationToken);
}
```

New private method `HandlePostPlayYes()`:
1. Get the last-played item from library (via state.ItemId)
2. Resolve JellyfinUser from session.UserId
3. Call `FindRadioTracksAsync()` to get genre-similar tracks
4. Shuffle and limit results (max 15)
5. Set up NowPlayingQueue with results
6. Enable `RadioModeState` for gapless continuation
7. Return `BuildAudioPlayerResponse(ReplaceAll, streamUrl, itemId, item, user, context)` with announcement speech

YesIntentHandler already has `_libraryManager` and `_userManager` injected.

### NoIntentHandler changes:
Add PostPlayState check as **HIGHEST priority** in both `HandleAsync` overloads:

In the overload WITHOUT sessionAttributes (line ~52):
```csharp
if (PostPlayState.TryGet(session.UserId, context.System.Device.DeviceID, out _))
{
    PostPlayState.Remove(session.UserId, context.System.Device.DeviceID);
    return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("PostPlayNoResponse", GetLocale(request))));
}
```

In the overload WITH sessionAttributes (line ~69):
Same check at the top, before resume/disambiguation checks.

**Write tests first (TDD)**:
- Yes with Ask state → PostPlayState removed → FindRadioTracksAsync called → RadioModeState enabled → returns AudioPlayer.Play + speech
- Yes with Ask state but no related tracks found → returns MediaNotFound
- Yes without Ask state → falls through to existing resume/pagination/disambiguation
- No with Ask state → PostPlayState removed → returns PostPlayNoResponse speech
- No without Ask state → falls through to existing resume/disambiguation
- Stale Ask state (>2 min) → falls through (TryGet returns false)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 YesIntentHandler checks PostPlayState before resume/pagination/disambiguation
- [x] #2 Yes with Ask state: finds related tracks, enables RadioModeState, returns AudioPlayer.Play with announcement
- [x] #3 Yes with Ask state but no tracks: returns MediaNotFound
- [x] #4 NoIntentHandler checks PostPlayState before resume/disambiguation
- [x] #5 No with Ask state: removes state, returns PostPlayNoResponse
- [x] #6 Both handlers fall through correctly when no PostPlayState exists
- [x] #7 dotnet build passes with zero warnings
- [x] #8 All handler tests pass
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
