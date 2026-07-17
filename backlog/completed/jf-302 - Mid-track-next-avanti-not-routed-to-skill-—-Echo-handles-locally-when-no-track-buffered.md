---
id: JF-302
title: >-
  Voice STOP/CANCEL not delivered by Echo during AudioPlayer — platform
  limitation (default-music-slot competition)
status: Done
assignee: []
created_date: '2026-07-02 16:09'
updated_date: '2026-07-02 20:13'
labels:
  - bug
  - playback
  - audio-player
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10'
  - JF-301
  - JF-304
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
During AudioPlayer playback, voice STOP and CANCEL never reach the skill, so the plugin cannot honor them. NOTE: "next" (avanti) was a SEPARATE issue and has been split to JF-304 — it has a plausible plugin-side fix (eager prefetch); THIS task does not.

SYMPTOM: Saying "alexa stop" / "ferma" / "interrompi" while music plays does nothing — audio continues. Only "pausa" works (routes as AMAZON.PauseIntent to the active AudioPlayer skill).

EVIDENCE (on-device 2026-07-02, session 76e19023): across many stop attempts, the plugin received ZERO StopIntent / CancelIntent / PlaybackController / SessionEnded / PlaybackStopped events. "Pausa" routed as PauseIntent and worked (AudioPlayerStop).

ROOT CAUSE (proven via Alexa web simulator SkillDebugger): typing "ferma" returned ConsideredIntents = [{ name: "<IntentForDifferentSkill>" }]. Alexa routes generic music commands (stop/next) to the user's DEFAULT MUSIC SERVICE (Amazon Music/Spotify), not to Jellyfin. "Pausa" works because pause routes to the ACTIVE AudioPlayer skill.

Every plugin-controlled layer verified CORRECT: (1) manifest declares AUDIO_PLAYER; (2) NLU routes ferma/stop -> AMAZON.StopIntent (profile-nlu confirmed); (3) PauseIntentHandler stops audio on Stop/Cancel via BuildPauseResponse (AudioPlayer.Stop + ShouldEndSession=true); (4) model deployed, build SUCCEEDED. The failure is purely Echo-side delivery.

NOT FIXABLE IN PLUGIN CODE: custom AudioPlayer skills cannot claim the device's default-music slot (same platform family as "custom skills get no seek bar"). NOT a regression — affects all released versions including pre-0.9.2.0.

WORKAROUND (account-side only): Alexa app -> Music & Podcasts -> change/disable the competing default music service; or use "pausa"; or one-shot "ask <invocation> to stop".

Documented in CLAUDE.md Key Gotchas ("Stop/Next/Previous + content switching during playback → default music service"). See JF-304 for the separate, potentially-fixable "next" issue.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Root cause proven via Alexa web simulator: 'ferma' -> ConsideredIntents <IntentForDifferentSkill> (default-music-service competition), 2026-07-02
- [x] #2 All plugin-controlled layers verified correct: manifest declares AUDIO_PLAYER; NLU routes ferma/stop -> AMAZON.StopIntent (profile-nlu); PauseIntentHandler stops on Stop/Cancel; model build SUCCEEDED
- [x] #3 Conclusion recorded: not code-fixable (custom AudioPlayer skills can't claim default-music slot); documented in CLAUDE.md Key Gotchas
- [x] #4 Account-side workaround captured: disable/change competing default music service, or use 'pausa' / one-shot invocation
- [x] #5 'Next' (avanti) split to JF-304 — different root cause, candidate plugin-side fix
<!-- AC:END -->





## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Investigation 2026-07-02 (stop): NOT a model/handler gap. PauseIntentHandler already handles Pause+Stop+Cancel (PauseIntentHandler.cs:39-40,58-59). AMAZON.StopIntent + CancelIntent present in all 17 LOCAL models AND in the DEPLOYED it-IT model (57 intents). SMAPI get-skill-status = SUCCEEDED for all build steps (LANGUAGE_MODEL_FULL_BUILD, DIALOG_MODEL_BUILD, etc.) — the plugin's LocaleModelStatuses='IN_PROGRESS' is a stale local cache, NOT a real incomplete build. Conclusion: the plugin/model side is fully correct; the Echo simply routes PauseIntent but NOT StopIntent/NextIntent during AudioPlayer playback. This is the Alexa transport-interface layer — the skill does not declare Alexa.PlaybackController (no matches in Alexa/Manifest/). Leading fix candidate: declare Alexa.PlaybackController (+ handle its directives). Needs Alexa-docs grounding + on-device testing; NOT a quick edit.

RESEARCH CORRECTION 2026-07-02 (Amazon docs): PlaybackController is NOT a fix for voice transport. Per the PlaybackController Interface Reference: 'PlaybackController requests are NOT sent in response to voice requests such as Alexa, next song — voice requests are sent as built-in intents (AMAZON.NextIntent) via IntentRequest.' PlaybackController is only for hardware buttons/remote/on-screen taps, and has NO Stop operation (only Next/Pause/Play/Previous). So implementing Alexa.PlaybackController would not fix voice 'stop' or voice 'next' — drop that candidate.

Per AudioPlayer Interface Reference: 'PlaybackStopped is sent when Alexa stops playing in response to a VOICE REQUEST or an AudioPlayer directive.' So voice 'Alexa, stop' during AudioPlayer should make the Echo stop locally + send PlaybackStopped (NOT StopIntent). The plugin received neither → the Echo never acted on 'alexa stop'.

Leading remaining cause = UTTERANCE/LOCALE: user said English 'alexa stop' on an it-IT device. Italian stop = 'ferma'/'fermati'. 'pausa' worked because it's unambiguously Italian. ACTION: have user test 'alexa ferma' — if that fires PlaybackStopped and stops audio, this is an utterance/locale matter, NOT a plugin bug (nothing to fix in code). JF-302 should be reframed from 'implement PlaybackController' to 'transport not recognized' once ferma test confirms.

NLU PROFILER RESULT 2026-07-02: `ask smapi profile-nlu` for it-IT: 'ferma' -> selectedIntent.name = AMAZON.StopIntent. So the deployed model/NLU correctly routes stop words to StopIntent. The model/NLU is NOT the problem.

MANIFEST: declares AUDIO_PLAYER (+ VIDEO_APP, ALEXA_PRESENTATION_APL) — manifest.json:7. So the AudioPlayer interface IS declared.

CONCLUSION — stop is NOT a plugin bug. Every plugin-controlled layer verified correct: (1) manifest declares AUDIO_PLAYER, (2) NLU routes 'ferma'/'stop' -> AMAZON.StopIntent, (3) PauseIntentHandler stops audio on StopIntent via BuildPauseResponse (AudioPlayer.Stop + ShouldEndSession=true), (4) model deployed + build SUCCEEDED. Yet the Echo delivers ZERO StopIntent and ZERO PlaybackStopped during AudioPlayer playback for voice stop (stop/ferma/interrompi all fail; 3 words rules out locale). Pause works only because the SKILL sends the AudioPlayer.Stop directive; voice 'stop' relies on the ECHO stopping locally + sending PlaybackStopped (per AudioPlayer doc) — and that is not happening. This is Alexa-platform delivery behavior the plugin cannot control (akin to the documented 'custom AudioPlayer skills get no seek bar / can't claim default-music slot' limitations). NOT fixable in plugin code. Reframe: 'voice stop/next not delivered by Echo during AudioPlayer — platform behavior'. Release (0.9.2.0) is not blocked: this affects all released versions and is not a regression.

ROOT CAUSE FOUND 2026-07-02 (web simulator SkillDebugger): typing 'ferma' in the Alexa web simulator produced ConsideredIntents = [{ 'name': '<IntentForDifferentSkill>' }]. Alexa routed 'ferma' to a DIFFERENT skill, not Jellyfin. This is skill-competition / default-music-slot: 'stop'/'ferma'/'next' are generic music commands that Alexa hands to the user's default music service (Amazon Music/Spotify), which isn't playing -> nothing happens. 'pausa' works because pause routes to the ACTIVE AudioPlayer skill (Jellyfin). NOT shouldEndSession (that chase was a dead end for this path — voice Play responses are shouldEndSession=true with no APL). NOT plugin-fixable: custom AudioPlayer skills cannot claim the device default-music slot (per CLAUDE.md). User workaround: change/disable the competing default music service in Alexa app settings, or use 'pausa'. Close JF-302 as a platform limitation (won't-fix in code), not a code task.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Investigated conclusively 2026-07-02. Root cause = skill competition: the Alexa web simulator showed "ferma" -> ConsideredIntents <IntentForDifferentSkill>, i.e. Alexa routes stop/next to the user's default music service, not the Jellyfin skill. Every plugin layer verified correct (manifest declares AUDIO_PLAYER; NLU routes ferma/stop to AMAZON.StopIntent; PauseIntentHandler stops audio on Stop/Cancel; model build SUCCEEDED). Not code-fixable — custom AudioPlayer skills cannot claim the device default-music slot. Not a regression (affects all released versions). "Next" (avanti) was split to JF-304 because it has a different root cause and a candidate plugin-side fix (eager prefetch). Closed as a documented platform limitation; user workaround is account-side (disable competing default music service) or use "pausa".
<!-- SECTION:FINAL_SUMMARY:END -->

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
