---
id: JF-242
title: 'PostPlayBehavior: PlaybackNearlyFinished + PlaybackFinished handlers'
status: Done
assignee: []
created_date: '2026-06-02 12:05'
updated_date: '2026-06-02 14:08'
labels:
  - feature
  - tdd
dependencies:
  - JF-241
references:
  - /home/pantinor/.cc-mirror/zai/config/plans/cozy-sleeping-wreath.md
documentation:
  - Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
  - Alexa/Handler/Event/PlaybackFinishedEventHandler.cs
  - Alexa/RadioModeState.cs
  - Alexa/Handler/BaseHandler.cs (FindRadioTracksAsync)
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the core PostPlay behavior logic in the AudioPlayer event handlers. **Write tests first (TDD).**

### PlaybackNearlyFinishedEventHandler changes (line ~108):
When `nextItemId == null` (queue exhausted) AND radio mode is NOT already on:
- Get effective PostPlayBehavior via `GetPostPlayBehavior(user)`
- If **AutoPlay** or **Ask**: `PostPlayState.Set(userId, deviceId, mode, currentAudioItemId)` → return `ResponseBuilder.Empty()`
- If **Stop**: return `ResponseBuilder.Empty()` (current behavior, no state set)

### PlaybackFinishedEventHandler changes:
**Add ILibraryManager + IUserManager dependencies** to constructor (follow PlaybackNearlyFinishedEventHandler pattern). Update DI registration.

After `OnPlaybackStopped` call (line ~48), before `hasQueuedNext` check:
- Check `PostPlayState.TryGet(userId, deviceId)` 
- If **AutoPlay mode**:
  - Get the last-played audio item from library
  - Call `FindRadioTracksAsync()` to find genre-similar tracks
  - Set up NowPlayingQueue with results
  - Enable `RadioModeState` (for gapless continuation of subsequent tracks)
  - Return speech "Playing more music by {artist}" + `AudioPlayer.Play(ReplaceAll, firstTrack)` + `ShouldEndSession=true`
- If **Ask mode**:
  - Return `ResponseBuilder.Ask("PostPlayAskPrompt", new Reprompt("PostPlayAskReprompt"))` 
  - This returns `ShouldEndSession=false` → starts a NEW Alexa session for Yes/No
- If **no state** (Stop mode): fall through to existing behavior (`BuildEndSessionResponse()`)

### DI Registration:
Update PlaybackFinishedEventHandler registration to inject ILibraryManager + IUserManager.

**Write handler tests first (TDD)**:
- Stop mode: queue exhausted → PlaybackNearlyFinished returns empty → PlaybackFinished ends session
- AutoPlay: NearlyFinished sets PostPlayState → Finished finds related tracks → returns speech + Play directive + enables RadioModeState
- AutoPlay announcement includes artist name from the last-played item
- Ask mode: NearlyFinished sets PostPlayState → Finished returns Ask prompt with ShouldEndSession=false
- Radio mode already on → PostPlay does NOT trigger (radio takes precedence)
- Queue not exhausted → PostPlay does NOT trigger
- PostPlayState expired (>2 min) → falls through to Stop behavior
- No related tracks found (AutoPlay) → falls through to Stop behavior
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 PlaybackNearlyFinished sets PostPlayState when queue exhausted and mode is AutoPlay or Ask
- [x] #2 PlaybackNearlyFinished does NOT set PostPlayState when radio mode is already on
- [x] #3 PlaybackFinished AutoPlay mode returns speech announcement + AudioPlayer.Play(ReplaceAll) + ShouldEndSession=true
- [x] #4 PlaybackFinished AutoPlay enables RadioModeState for subsequent gapless continuation
- [x] #5 PlaybackFinished Ask mode returns speech prompt + ShouldEndSession=false (new session)
- [x] #6 PlaybackFinished Stop mode (no state) behaves as before (BuildEndSessionResponse)
- [x] #7 Stale PostPlayState (>2 min) falls through to Stop behavior
- [x] #8 AutoPlay falls back to Stop if no related tracks found
- [x] #9 ILibraryManager and IUserManager injected into PlaybackFinishedEventHandler
- [x] #10 DI registration updated
- [x] #11 dotnet build passes with zero warnings
- [x] #12 All handler tests pass
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
