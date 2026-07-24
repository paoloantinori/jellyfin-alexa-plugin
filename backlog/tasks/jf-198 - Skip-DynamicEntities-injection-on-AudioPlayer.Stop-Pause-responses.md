---
id: JF-198
title: Skip DynamicEntities injection on AudioPlayer.Stop/Pause responses
status: Done
assignee:
  - Claude
created_date: '2026-05-21 20:23'
updated_date: '2026-05-21 20:47'
labels:
  - bug
  - performance
milestone: Resume Improvements
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PauseIntentHandler.cs
documentation:
  - >-
    Alexa Dialog.UpdateDynamicEntities directive:
    https://developer.amazon.com/en-US/docs/alexa/custom-skills/dialog-management.html
  - >-
    Alexa AudioPlayer.Stop directive:
    https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html#stop
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

The `DynamicEntitiesInterceptor` attaches a `Dialog.UpdateDynamicEntities` directive to **every** skill response, including pause/stop responses that already carry an `AudioPlayer.Stop` directive. When Alexa processes a response containing both `AudioPlayer.Stop` + `Dialog.UpdateDynamicEntities` + `ShouldEndSession=false`, the session enters a dormant state (audio stopped, no active dialog). After ~15 seconds Alexa's session timeout triggers and it reports `InternalServiceError: Internal Server Error`.

This is visible in logs as:
- PauseIntent response: ~5957 bytes, 2 directives (AudioPlayerStop + DynamicEntities)
- 15 seconds later: `SessionEndedRequest` with `Error - InternalServiceError: Internal Server Error`

Every PauseIntent produces this pattern (consistent ~5957 bytes, 2 directives), confirming the interceptor always adds the DynamicEntities directive.

## Root Cause

`DynamicEntitiesInterceptor.cs` (line 97-98) unconditionally injects into any response that has a non-null `Response.Directives` list. It doesn't check whether the response is a "terminal" audio directive (Stop/ClearQueue) where dialog state is irrelevant.

## Affected Code

- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs` — the interceptor that adds DynamicEntities to responses
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PauseIntentHandler.cs` — uses `BuildPauseResponse()` which returns `AudioPlayerStop` + `ShouldEndSession=false`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` — `BuildPauseResponse()` at line 400

## Implementation Plan

### Phase 1: Skip DynamicEntities for AudioPlayer.Stop responses

In `DynamicEntitiesInterceptor.cs`, add a guard before injecting the directive:

```csharp
// Skip DynamicEntities for AudioPlayer.Stop/ClearQueue responses — no active dialog
if (context.Response?.Response?.Directives?.Any(d =>
    d is AudioPlayerStopDirective or AudioPlayerClearQueueDirective) == true)
{
    return;
}
```

This check goes at the top of the interceptor method, before any DB queries or directive building. It short-circuits the entire interceptor for pause/stop responses.

**Why this position**: The guard must run BEFORE `ResolveUserWithLibraries()` and `Build()` to avoid the DB query overhead on every pause. The interceptor currently does a full user lookup + dynamic entities build even though the result will be discarded for pause responses.

### Phase 2: Verify no other interceptors add directives to pause responses

Search for any other interceptors or post-processing steps that inject directives into responses. If found, apply similar guards.

### Phase 3: Test

- Unit test: verify PauseIntent response has exactly 1 directive (AudioPlayerStop) when interceptor is active
- Unit test: verify non-pause responses still get DynamicEntities directive
- Live test: pause playback and confirm no SessionEndedRequest error in logs

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PauseIntentHandler.cs`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` (line 400, `BuildPauseResponse`)

## References
- Alexa `Dialog.UpdateDynamicEntities` docs: only meaningful when dialog is active
- Alexa `AudioPlayer.Stop`: stops playback, session may stay open if `ShouldEndSession=false`
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 DynamicEntitiesInterceptor skips injection when response contains AudioPlayerStopDirective
- [x] #2 DynamicEntitiesInterceptor skips injection when response contains AudioPlayerClearQueueDirective
- [x] #3 Pause response has exactly 1 directive (AudioPlayerStop) — no DynamicEntities
- [x] #4 Non-pause responses (play, browse, search) still receive DynamicEntities directive
- [x] #5 Unit test confirms skip behavior for stop/pause responses
- [ ] #6 Live test: pause playback produces no SessionEndedRequest error within 20 seconds
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Phase 1: Add guard in DynamicEntitiesInterceptor.cs
- Add a guard immediately after the existing AudioPlayerPlayDirective check (line 57) to skip injection for AudioPlayerStopDirective and AudioPlayerClearQueueDirective
- Both types already imported via `using Alexa.NET.Response.Directive;`

### Phase 2: Add unit tests
- Add `ProcessAsync_AudioPlayerStopDirective_SkipsDynamicEntities` test
- Add `ProcessAsync_AudioPlayerClearQueueDirective_SkipsDynamicEntities` test
- Follow existing `ProcessAsync_AudioPlayerPlayDirective_SkipsDynamicEntities` pattern (line 307)

### Phase 3: Build, test, verify on local instance, commit
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed DynamicEntitiesInterceptor to skip injection on AudioPlayer Stop/ClearQueue responses. Combined all three audio-directive guards into a single .Any() check. Consolidated three near-duplicate tests into a [Theory]. Verified on local Jellyfin: PauseIntent returns exactly 1 directive (AudioPlayer.Stop), no DynamicEntities attached.

Note: The TaskCanceledException in logs (corr=e4cf5467) is a pre-existing timeout in DynamicEntities build for a non-pause request, unrelated to this fix.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
