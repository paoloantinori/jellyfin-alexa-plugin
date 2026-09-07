# Research Report: Session state and stop/pause routing during VideoApp and AudioPlayer playback

**Date**: 2026-09-07
**Depth**: exhaustive
**Confidence**: HIGH on the documented mechanics (official Amazon docs, current as of Aug 2026); HIGH on our own device evidence (podman-log verified timestamps); MEDIUM on the community corroboration.

## Executive Summary

An open session DOES change how stop is routed, and the mechanism is documented: a response that omits `shouldEndSession` (which the VideoApp docs mandate) leaves an extended session open for up to 30 more seconds on a screen device, with the microphone closed; inside that window, "Alexa, <utterance>" (wake word required) routes to the skill as a continuing session, while speech without the wake word is ignored. This exactly explains our "every tanto va" pattern: the only stop that ever reached us during video arrived +19s after the VideoApp.Launch, inside the window. We are not leaving a wrong session state: our launch responses are docs-compliant and already maximize the window. Past those 30 seconds there is no session shape, directive, or interface we control that makes stop/next/previous reach a custom skill during playback, and even when a stop DOES arrive during video the skill cannot stop the video, because the VideoApp interface has exactly one directive, Launch, and no stop or pause directive exists.

## Findings

### 1. VideoApp.Launch response semantics (official docs)

From the VideoApp Interface Reference (last updated Aug 17, 2026):

- "Do not include the `shouldEndSession` parameter in the response, even if you set the value to null." Our builders comply: `BuildVideoAppLaunchResponse` and the video-audio builders set `ShouldEndSession = null`, which Alexa.NET omits from the JSON. Verified in the live response-body logs for both launches analyzed below: `shouldEndSession=ABSENT`.
- The interface provides exactly ONE directive: `VideoApp.Launch`. There is NO `VideoApp.Stop`, no pause directive, no queue control. Once video starts the skill cannot programmatically dismiss or pause the player.
- `AMAZON.CancelIntent` is not supported with VideoApp. The back button is displayed on every VideoApp screen and cannot be hidden.
- The doc claims these standard built-in intents "work with VideoApp": `AMAZON.PauseIntent` and `AMAZON.StopIntent` ("which send the same message to the skill"), plus `AMAZON.ResumeIntent`. Voice controls listed for the player: "Alexa, pause/resume" and "Alexa, stop/close". Our device evidence supports the routing part of this claim only inside the 30-second session window (below); outside it we have never observed a stop arriving.

### 2. What an omitted shouldEndSession actually does (official docs)

From the Request and Response JSON Reference (`shouldEndSession` property) and the Manage Skill Sessions doc:

- `true`: the session ends.
- `false` or `null` present in the JSON: the microphone opens for a few seconds (reprompt flow).
- **absent (undefined)**: "The session's behavior depends on the type of Echo device. If the device has a screen and the skill displays screen content, the session stays open for up to 30 more seconds, without opening the microphone to prompt the user for input. If the user speaks and precedes their request with the wake word (such as 'Alexa,') Alexa sends the request to the skill. Otherwise, Alexa ignores the user's speech."
- The extended-session rules require: a screen device, the skill has APL (or the deprecated Display) interface enabled (ours does; we send APL documents), and the response includes screen content. `undefined` gives approximately 30 seconds; `false` gives 20 to 30 seconds after the reprompt fails.
- Also documented: "Responses to `AMAZON.StopIntent` must use `true`."

So on the Echo Show, our compliant VideoApp.Launch responses automatically buy a 30-second window in which wake-word-prefixed commands reach the skill without the invocation name, as a continuing session. That is the ONLY session-mediated routing that exists around video playback, and there is no way to extend it: no keepalive request or directive exists for VideoApp.

### 3. Our live evidence, decoded (podman logs, 2026-09-07 device session)

Timeline extracted from the Jellyfin container logs (`LoggingRequestInterceptor` + `ResponseBodyLoggingInterceptor`):

| Time | Event | Session shape |
|------|-------|---------------|
| 14:11:34 | `PlayVideoIntent` "Inside Out 2" (one-shot) | `new:true` |
| 14:11:35 | VideoApp.Launch response | `shouldEndSession` ABSENT, no reprompt (compliant) |
| **14:11:54** | **`AMAZON.StopIntent` arrives** | same session, `new:false` (continuing session), +19s after launch |
| 14:23:13 | `LaunchRequest` (interactive open) | `new:true` |
| 14:23:24 | `AMAZON.NoIntent` declines resume offer | our response: `shouldEndSession=false` + reprompt (open session) |
| 14:23:31 | `PlayVideoIntent` (same session) | VideoApp.Launch response: `shouldEndSession` ABSENT (compliant, identical shape to 14:11:35) |
| 14:23:31 to 14:25:20 | user repeatedly tries to stop | **ZERO stop requests arrive** for ~110s of video playback |
| 14:25:20 | `SessionEndedRequest` closes that session | |
| 14:25:35 | `PlayArtistSongsIntent` (music, new session) | |
| 14:25:49 | `AMAZON.PauseIntent` arrives mid-playback | `new:true` (auto-routed during AudioPlayer, as documented) |

Reading of the two video sessions:

- The one stop that routed (14:11:54) sits at +19s, inside the documented 30-second extended-session window, and arrived as a continuing-session request (same sessionId, `new:false`). This is the documented undefined-shouldEndSession mechanism working exactly as written.
- The failing session (14:23) launched video from a session that had an open reprompt exchange before it, but its launch response had the IDENTICAL shape (shouldEndSession absent, no reprompt) to the successful one. Per the docs the latest response governs the session state, so the prior open/closed state did not change the launch's own session semantics. The difference between the two outcomes is timing: the user's stop attempts in the second session fell outside the 30-second window (and/or lacked the wake word), where the documented behavior is "Alexa ignores the user's speech".
- Prior verified evidence still stands (2026-07-02, on-device): during AudioPlayer playback only Pause/Resume auto-route to the skill; Stop/Next/Previous are claimed by the device's default music service (simulator `ConsideredIntents` = `<IntentForDifferentSkill>`). The 14:25:49 PauseIntent arrival confirms pause routing again.

### 4. Stop arriving does not mean the video stops

- There is no stop directive for VideoApp (finding 1). At 14:11:54 our handler answered the stop with `AudioPlayer.Stop` + session end (correct per the docs, which require `true` on stop responses), and the video kept playing; the user reports stops were never honored.
- Community corroboration: StackOverflow 60500295 (a VideoApp skill) reports the same shape from the other side: StopIntent DOES arrive during video playback, but answering with speech alone leaves the video streaming. There is no accepted programmatic stop.
- StackOverflow 47592390 (2017, German locale) reports the complementary case: skill never receives StopIntent as an IntentRequest at all, only `SessionEndedRequest` (USER_INITIATED).

The documented exits from a VideoApp player are the on-screen back button and the platform's own "Alexa, stop/close" voice control. Whether the platform natively closes the player when it routes a stop to the skill is not documented; our single data point and SO 60500295 both indicate it does not.

## Confidence Assessment

- HIGH: the shouldEndSession=undefined 30-second closed-mic session window and its wake-word routing rule (stated identically in two official docs).
- HIGH: VideoApp.Launch must omit shouldEndSession; our responses comply (verified from live response bodies, not from code reading alone).
- HIGH: no VideoApp stop/pause directive exists (the interface reference lists exactly one directive).
- HIGH: our timeline and session shapes (extracted from podman logs this session).
- MEDIUM: the community reports (old, single-thread, no Amazon confirmation).
- UNVERIFIABLE from our logs: whether the user prefixed "Alexa," on each stop attempt, and the exact second of each attempt. The 30-second-window explanation covers both observed outcomes without needing the open-reprompt-session hypothesis; the identical launch-response shapes in the two sessions argue against our session handling being the differentiator.

## The verdict on "il problema siamo noi"

Partially, and in the opposite direction from suspected: our session handling is already optimal on the video path. The launch responses are compliant and buy the maximum documented routable window. The findings that matter for future work:

1. Nothing plugin-side extends routability past ~30s after a VideoApp.Launch, or makes stop/next/previous arrive during AudioPlayer playback (default-music-slot claim, platform limit).
2. Even a successfully routed stop cannot stop the video; the only programmatic-adjacent option would be outside the documented interface.
3. One untested device probe worth running: does "Alexa, chiudi" (close) during VideoApp playback dismiss the player natively? The docs list "stop/close" as the player's voice controls; if chiudi works it is the user-facing instruction to document.

The consolidated operational reference from this report now lives in the project CLAUDE.md section "Stop / Session Routing During Playback (THE REFERENCE)".

## Sources

1. VideoApp Interface Reference, developer.amazon.com/en-US/docs/alexa/custom-skills/videoapp-interface-reference.html (last updated Aug 17, 2026)
2. Request and Response JSON Reference, developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html
3. Manage Skill Sessions and Session Attributes, developer.amazon.com/en-US/docs/alexa/custom-skills/manage-skill-session-and-session-attributes.html
4. StackOverflow 60500295, "In alexa video skill how to stop addVideoAppLaunchDirective" (2020)
5. StackOverflow 47592390, "Alexa doesnt send request with AMAZON.StopIntent" (2017)
6. Internal: Jellyfin container logs, device session of 2026-09-07 (14:11 and 14:23 sessions); on-device AudioPlayer routing verification of 2026-07-02; project CLAUDE.md prior gotchas (JF-299, JF-387)
