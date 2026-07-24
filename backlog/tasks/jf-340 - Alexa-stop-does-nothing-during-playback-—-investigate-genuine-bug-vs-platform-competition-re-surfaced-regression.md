---
id: JF-340
title: >-
  "Alexa stop" does nothing during playback — investigate genuine-bug vs
  platform competition (re-surfaced regression)
status: To Do
assignee: []
created_date: '2026-07-13 10:32'
updated_date: '2026-07-14 17:12'
labels:
  - playback
  - stop
  - pause
  - routing
  - platform
  - investigation
  - regression
dependencies: []
references:
  - backlog/tasks/jf-299*
  - backlog/tasks/jf-302*
  - backlog/tasks/jf-157*
  - backlog/tasks/jf-198*
  - backlog/tasks/jf-277*
  - >-
    CLAUDE.md (Key Gotchas: Stop vs Pause / AudioPlayer responses /
    Stop-Next-Previous competition)
  - 'memory: feedback_should_end_session'
  - 'memory: feedback_apl_carousel_session'
  - >-
    https://developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html
  - >-
    https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PauseIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
  - CLAUDE.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User reports (2026-07-13) that "alexa stop did nothing, yet again" — a long-standing intermittent issue the user is convinced is a GENUINE bug, not (only) the documented platform skill-competition. Track the investigation here.

CURRENT EVIDENCE (2026-07-13, during Jazz Cafe playback after the JF-336/JF-338/continuation work):
- The Jazz Cafe play response had shouldEndSession=TRUE (verified in response body) — so NOT the JF-299 regression (open session breaking routing).
- A "stop" that DID reach the skill (12:19:00) was handled correctly: PauseIntent handler → {"shouldEndSession":true,"directives":[{"type":"AudioPlayer.Stop"}]} → PlaybackStopped fired, and NO AudioPlayer.Play directive followed (no prefetch-restart race in that instance).
- So the skill handles every stop it RECEIVES correctly. The failing "alexa stop" attempts are the ones that never reach the skill (no plugin log entry).

ANALYSIS 2026-07-13 — what we got wrong so far:
1. "No plugin log entry" was read as "not routed by the platform (skill competition)", but the diagnostic is ambiguous: an empty plugin log is compatible with (1) wake word/ASR never captured the utterance, (2) captured but handled by another service/device (Amazon Music, local device, another Echo), (3) routed but lost in transport (reverse proxy / TLS / endpoint timeout — proxy logs were NEVER checked), (4) playback was in VideoApp mode (stop handled on-device by design, no skill request expected — GetVideoAppForAudio, BaseHandler.cs:536), (5) reached the plugin and mishandled (never observed). The Alexa app Voice History (Settings → Alexa Privacy → Review Voice History) — which shows what Alexa heard and WHO responded — was never used; it is the diagnostic that separates 1/2/3.
2. The "stop = skill competition, not fixable" framing over-generalizes vs CURRENT Amazon docs (use-long-form-audio.html, verified 2026-07-13): "During audio streaming, users can control playback without the skill invocation name", and "When your skill isn't in an active session but is playing audio, or was the skill most recently playing audio, utterances such as 'Alexa, stop' cause Alexa to send the AMAZON.PauseIntent". So stop SHOULD always route to the skill (as PauseIntent) while it is playing / most-recent audio skill — unlike Next/Previous/content-switching, which remain structurally contested. Most-recent-audio-skill status is LOST when "the user invokes another service that streams audio" (e.g. a mid-playback content request that fell through to Amazon Music) — a plausible mechanism for intermittency. Also: the 2026-07-02 simulator evidence (ConsideredIntents=<IntentForDifferentSkill>) is methodologically invalid for playback-time routing, because simulate-skill carries no AudioPlayer / most-recent-skill state.
3. The prefetch/stop race theory (old AC#5) is unlikely as a mechanism: the plugin already sets ExpectedPreviousToken on ENQUEUE (BaseHandler.cs:561-564), which is exactly Amazon's documented anti-race protection ("Playlist progression with ENQUEUE" — mismatched token → device ignores the directive), and an ENQUEUE never STARTS playback by itself (inference from docs; confirm on-device only if a restart-after-stop is ever observed). Confirmed PlaybackNearlyFinishedEventHandler has no stop-guard and stop does not clear NowPlayingQueue/QueueContinuationStore/DeviceQueueManager (Clear only called by ClearQueueIntent) — but that gap's symptom would be a wrong continuation on a later resume, NOT "stop does nothing". Do NOT clear the queue on stop (it would break resume). JF-338 (longer active queue) is therefore almost certainly unrelated.
4. Minor task errors fixed: StopIntentHandler.cs does not exist (Stop/Cancel/Pause are all handled by PauseIntentHandler.cs, CanHandle lines 34-42); old AC#4 (routed stop must send AudioPlayer.Stop + ShouldEndSession=true) is already satisfied and proven by the 12:19 instance.

HISTORICAL DATA ON THIS TOPIC (refer here, don't re-derive):
- CLAUDE.md "Key Gotchas": "Stop vs Pause", "AudioPlayer responses", "Stop/Next/Previous + content switching during playback → default music service" — on-device 2026-07-02: zero StopIntent/NextIntent/PlaybackStopped for "stop"/"ferma"/"avanti". NOTE: the stop part of that gotcha needs correction per ANALYSIS point 2 after the next capture.
- Backlog: JF-299 (no shouldEndSession=false with unsupported directives), JF-302 (mid-track "avanti"/next not routed), JF-157 (pause/resume preserve state), JF-198 (skip DynamicEntities on Stop/Pause), JF-277 (sleep timer), JF-239 (NLU tests for built-ins).
- Memories: feedback_should_end_session, feedback_apl_carousel_session.

OPEN QUESTION to resolve: classify the next failing "alexa stop" instance into one of the 5 branches above with full evidence — do not re-assert "platform behavior" without it. Capture protocol: enable Debug logging ("Jellyfin.Plugin.AlexaSkill": "Debug" in /config/logging.default.json, restart container); on the next failure record exact timestamp, what the Echo did, WHICH Echo answered, playback mode (AudioPlayer vs VideoApp); collect Alexa app Voice History for that instant + podman logs jellyfin + reverse-proxy access logs for /alexaskill hits.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Capture a FAILING 'alexa stop' instance with mandatory evidence: exact timestamp; what the Echo did (audio kept playing? spoke? screen?); WHICH Echo device answered; playback mode (AudioPlayer vs VideoApp, from the Play response/logs); Alexa app Voice History entry for that instant (what Alexa heard + which skill/service responded); Jellyfin plugin logs (Debug enabled) for the window; reverse-proxy access logs for /alexaskill hits.
- [ ] #2 Classify the captured failure into one of 5 branches — do not assume: (1) utterance never captured (no Voice History entry); (2) captured but handled by another service/device (Voice History shows another responder, no proxy hit); (3) routed but lost in transport (proxy hit with no plugin log, or error/timeout); (4) playback was VideoApp — stop handled on-device by design, no skill request expected; (5) reached the plugin and mishandled (plugin log present — never observed so far).
- [ ] #3 Act per branch: 1/2 → update the CLAUDE.md gotcha distinguishing stop (routable per docs — arrives as AMAZON.PauseIntent while the skill is playing / most-recent audio skill; intermittent loss = loss of that status or upstream failure) from Next/Previous (structural competition), and document workarounds; 3 → fix infrastructure (proxy/timeout); 4 → document VideoApp stop behavior, consider UX implications; 5 → fix the handler + regression test (only branch that touches C# code).
- [ ] #4 Reflect the 2026-07-13 docs verification in CLAUDE.md after the capture: use-long-form-audio.html says 'Alexa, stop' during/after skill audio routes to the skill as AMAZON.PauseIntent; the 'not fixable platform competition' framing over-generalized for stop; the 2026-07-02 simulator evidence (ConsideredIntents) is invalid for playback-time routing because simulate-skill carries no AudioPlayer state.
- [ ] #5 Prefetch/stop race DOWNGRADED (was AC#5): the plugin already sets ExpectedPreviousToken on ENQUEUE (BaseHandler.cs:561-564) — Amazon's documented anti-race protection — and ENQUEUE never starts playback on its own. No stop-guard as a symptom fix; do NOT clear the progressive queue on stop (breaks resume). Revisit only if a captured instance shows audio restarting after a HANDLED stop.
- [ ] #6 Cross-reference prior attempts to avoid re-treading dead-ends: JF-299 (shouldEndSession=false on events is rejected — must be null/true), JF-302 (mid-track next not routed when no track buffered), JF-157 (pause/resume preserve playback state), JF-198 (skip DynamicEntities on Stop/Pause responses). Confirm none regressed.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Live repro 2026-07-14 ~19:07 (clean — skill was the unambiguously active player)

- 19:07:13 PlayAlbumIntent -> AudioPlayer.Play (Jazz Cafe); 19:07:18 PlaybackStarted.

- User said 'alexa stop'; audio STOPPED, but the skill received NO stop/pause intent — only AudioPlayer.PlaybackStopped at 19:07:48 (offset 28433ms, playerActivity=STOPPED). Handler saved position + ended session correctly.

- i.e. the Echo handled the stop LOCALLY and notified the skill via PlaybackStopped. The plugin's stop-intent handler was not invoked at all.

## Convergent evidence — plugin exonerated

- Episode A (18:53:05): 'stop'/'pause' routed to skill as AMAZON.PauseIntent -> handler returned AudioPlayer.Stop + shouldEndSession=true -> PlaybackStopped. Correct.

- Episode B (19:07:48, clean repro): local stop -> only PlaybackStopped reached skill -> handler saved position + ended session. Correct.

- Failing 'twice' earlier (18:5x): ZERO skill entries — the request was neither routed to the skill nor handled locally.

- Conclusion: the plugin handles EVERY stop-related request it receives correctly (both the PauseIntent path and the PlaybackStopped notification path). The intermittent 'stop does nothing' is the Echo's routing/local-handling variability — platform behavior, NOT a plugin bug. Not fixable plugin-side (custom AudioPlayer skills cannot claim the device's default-music slot; PlaybackController serves hardware buttons only with no STOP op, per Amazon docs).

## Workaround

'pausa'/'pause' reliably routes to the active player. One-shot invocation 'chiedi a mia collezione ferma' (imperative, NOT infinitive 'fermare') also forces routing. Recommend closing JF-340 as confirmed platform behavior.

## UPDATE 2026-07-14 19:1x — WITHDRAW the 'close as platform behavior' recommendation above; time-dependence hypothesis is open

User observation right after the clean repro: 'the only difference [from the failing times] is that I have let it run for longer.' This is a sharp, mechanistically plausible discriminator. Both WORKING stops (18:53:05 at ~25s into the track; 19:07:48 at ~30s) occurred well AFTER PlaybackStarted. The failing 'twice' earlier had zero skill entries and their timing relative to playback start is unknown — but may have been very early.

HYPOTHESIS: 'alexa stop' fails during the early-playback window (first few seconds after AudioPlayer.Play, around/before PlaybackStarted settles) when the device has not yet firmly attributed 'most-recent audio skill' status / settled the AudioPlayer state. It works once playback is established. If reproducible, this is a TIME-DEPENDENT pattern, not random platform competition — and could have a plugin-side mitigation (Play-directive shape, progressive-response timing, anything that delays device attribution) or at minimum a precise user-facing rule.

DO NOT CLOSE JF-340. Validation repro in progress: Trial 1 & 2 = start a track, say 'alexa stop' within ~3s (predict fail, zero skill entries); Trial 3 = start a track, wait ~30s, say 'alexa stop' (predict works). If early-fails / late-works reproduces, investigate the early window for any plugin lever before concluding 'platform-only'.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
