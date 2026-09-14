# Research Report: Stop-intent handling and routing in Alexa AudioPlayer custom skills

**Date**: 2026-09-14
**Depth**: exhaustive (deep-research workflow: 99 agents, fan-out search + source fetch + 3-vote adversarial verification per claim)
**Confidence**: HIGH on the documented rules; the misrouting mechanism remains officially undocumented (community + our captures only)

## Executive Summary

Amazon documents precisely how bare transport commands behave around an AudioPlayer custom skill: eleven built-in intents are invocable without the invocation name during or after our playback (Pause and Resume mandatory, the other nine delivered even when absent from the schema) and StopIntent is deliberately NOT among them; a bare "Alexa, stop" is instead delivered to the most-recently-playing skill as AMAZON.PauseIntent, and halting is the skill's own job via the AudioPlayer.Stop directive. What Amazon does NOT document anywhere is the arbitration that sometimes gives the command to the default music service instead: the adversarial round refuted (1-2) the causal link between the documented memory-reset rule and the misrouting, so the "why sometimes Amazon Music wins" question stands on community reports and this project's device captures alone.

## Verified findings (adversarial votes)

1. (6-0) Bare "Alexa, stop" while the skill is playing audio or was the most recent to play, outside an active session, is delivered as AMAZON.PauseIntent, not StopIntent. Sources: use-long-form-audio (en-US + en-IN, live-verified), the ASK launch blog worked example.
2. (12-0) The documented invocation-name-free set is exactly eleven intents: Cancel, LoopOff, LoopOn, Next, Pause, Previous, Repeat, Resume, ShuffleOff, ShuffleOn, StartOver. The StopIntent row carries the without-invocation-name clause in 0 of 10 locale tables; it is defined solely as the skill-exit intent (response must end the session; Amazon's own two pages diverge between "must use true" and "true or null", false never permitted on either).
3. (verified) Memory boundary: Alexa remembers which skill started the stream (after the session ends) and routes bare voice and tap playback requests to it, until the user invokes playback with a different skill, another streaming service (explicitly including the built-in music service or a Flash Briefing), or reboots the device. On screen devices, a shouldEndSession-undefined response with screen content keeps the session open up to 30 more seconds, mic closed; wake-word-prefixed speech routes to the skill in that window, unprefixed is ignored.
4. (REFUTED 1-2) The claim that the memory-reset rule is "the documented mechanism behind" the misrouting to the default music service: no official page documents any arbitration rule for when the default music service wins during a custom skill's own active playback. The misrouting is evidenced by community reports (Stack Overflow 77272405, 77283282) and this project's 2026-07-02 and 2026-09-14 device captures (zero arrivals, ConsideredIntents = IntentForDifferentSkill).
5. (REFUTED 0-3) Stricter per-event response limits (e.g. "PlaybackStarted accepts only Stop/ClearQueue", "PlaybackStopped accepts no response"): only the general directives-only shape holds for AudioPlayer events (no outputSpeech, card, reprompt, or shouldEndSession); the stricter per-event variants must not be cited.
6. Partial coverage: for the manifest-interface question (does declaring PlaybackController/SeekController/MSAPI change bare-command routing) only PlaybackController claims survived verification; SeekController/MSAPI routing effects were not verified here. (Our own A/B probe, JF-560, has since closed the SeekController half empirically: not declarable on a custom skill manifest.)

## Caveats

- Community-side evidence includes a 2023 Stack Overflow report (no accepted answer) suggesting PlaybackStopped delivery itself can be flaky on real devices.
- The it-IT transport-word evidence carries the unresolved confound already documented in the project reference ("avanti" may not be a canonical NextIntent word; "successivo" re-test pending).
- Primary pages were live-verified 2026-09-14 with footer stamps from Sep 2024 to Aug 2026; older pages carry some staleness risk.

## Relation to this project

Everything verified is consistent with the CLAUDE.md reference section and today's device/console captures; two sharpenings were applied to it: the eleven-intent list made exact (StopIntent excluded, Pause/Resume required), and the causal note that the misrouting mechanism is undocumented (the memory rule must not be cited as its cause). The recommended user guidance is unchanged: pause always routes; stop needs the invocation name or the screen button.

## Sources

- https://developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html (+ en-IN copy)
- https://developer.amazon.com/en-US/docs/alexa/custom-skills/standard-built-in-intents.html
- https://developer.amazon.com/en-US/docs/alexa/custom-skills/request-and-response-json-reference.html
- https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html
- https://developer.amazon.com/en-US/blogs/alexa/post/Tx1DSINBM8LUNHY/new-alexa-skills-kit-ask-feature-audio-streaming-in-alexa-skill
- https://stackoverflow.com/questions/77272405 and /questions/77283282
- This project's captures: 2026-07-02 and 2026-09-14 device sessions, 2026-09-14 console session (ConsideredIntents list, stop-to-PauseIntent delivery)
