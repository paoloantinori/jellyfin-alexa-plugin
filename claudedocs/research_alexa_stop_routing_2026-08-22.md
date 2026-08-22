# Research Report: "Alexa, stop" intermittently failing for custom AudioPlayer skills

**Date**: 2026-08-22
**Depth**: exhaustive
**Confidence**: MEDIUM (docs confirmed; timing hypothesis NOT corroborated)

## Executive Summary

Amazon's official documentation guarantees stop routing to the audio-playing skill with no documented settling window and no documented difference between one-shot and interactive-session playback starts. No other Alexa skill project on GitHub documents a "stop fails shortly after a one-shot play, works later" pattern. What IS documented is a class of intermittent, Amazon-side routing incidents (PauseIntent/StopIntent silently not delivered, acknowledged by Amazon as "a known issue"). Our 3-data-point "30-second settling" conclusion is therefore unsupported and should be withdrawn as a causal claim; the honest state is "intermittent platform routing failure of unknown trigger."

## Findings

### 1. Official routing rules (HIGH confidence, primary sources)

From "Stream Long-Form Audio with AudioPlayer" (developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html):

> "When your skill isn't in an active session but is playing audio, or was the skill most recently playing audio, utterances such as 'Alexa, stop' cause Alexa to send the `AMAZON.PauseIntent` instead of the `AMAZON.StopIntent`."

- The rule is stated unconditionally: during playback, stop goes to the playing skill as PauseIntent. Nothing about how playback was STARTED (one-shot vs session).
- After playback ends, stop still routes to the last-playing skill as StopIntent (confirmed by alexa-samples PR #158, which exists precisely because skills receive StopIntent even when no longer playing).
- **No settling/transition window is documented anywhere** in the AudioPlayer interface reference or the long-form audio guide.
- Relevant adjacent rule found: if you send Play with `shouldEndSession=false`, "Alexa sends the stream to the device for playback, and then pauses the stream to listen for the user's response." (We already use true; JF-299.)

### 2. No corroboration of a timing window or session-mode dependency (HIGH confidence in the absence)

Searched: ASK SDK for Node.js issues, ASK SDK for Python issues, alexa-samples/skill-sample-nodejs-audio-player issues/PRs, music-assistant-alexa-skill-prototype issues, My Media for Alexa docs (bizmodeller), Stack Overflow, Amazon developer forums (old answerhub + new), bock-media (self-hosted music Alexa skill), Sonos community threads.

- None report "stop fails when said immediately after a one-shot play and works later."
- The sample-repo maintainer (sebsto, alexa-samples issue #58) states the platform contract plainly, again unconditionally and with no timing caveat: "Amazon forwards to the currently playing skill the 'next', 'previous', 'stop' etc. utterances."
- My Media for Alexa (the largest commercial self-hosted-music Alexa skill) documents plain "Alexa, stop / pause / resume / next" commands with no timing caveat or known-issue note.

### 3. Intermittent Amazon-side routing incidents ARE a documented class (MEDIUM confidence)

- Amazon developer forums thread "AMAZON.PauseIntent Not Working" (answerhub a78813, now offline; snippet captured by search): "If I say 'Alexa, pause' then the request never reaches my skill and Alexa responds with 'I'm not quite sure how to help you with that.' ... This is a known issue that started on Satur[day]". An Amazon-acknowledged incident where stop/pause routing silently broke for custom skills for a period.
- This establishes precedent: the platform does have episodes where voice stop/pause is not delivered to skills, independent of anything the skill does. But these were time-bounded outages, not a per-request timing window.

### 4. Default-music-service competition (HIGH confidence, consistent with our on-device 2026-07-02 finding)

- Custom AudioPlayer skills cannot claim the device's default-music slot (reserved for the Music/Radio/Podcast Skill API partners). Stop/next/previous during playback are "frequently claimed" by the default music service. Our CLAUDE.md already records verified on-device evidence of this (zero StopIntent events; simulator ConsideredIntents = IntentForDifferentSkill).
- This is a per-utterance NLU competition, which is inherently probabilistic. It is a plausible mechanism for intermittent stop failures that correlates with NOTHING observable skill-side. It would not explain why pause always routes, except that pause is documented as always routed to the active player (platform contract).

### 5. Workarounds found

- None beyond what we already ship/document: use "pause" (always routed), or the one-shot form "ask <skill> to stop". No directive, response field, or session trick improves stop routing.
- alexa-samples PR #158's pattern (check `context.AudioPlayer.playerActivity` before acting on StopIntent) addresses the AFTER-playback case, not this failure.

## Confidence Assessment

- HIGH: the documented routing contract (stop arrives as PauseIntent during playback, unconditional); absence of any documented settling window; absence of the pattern in other projects' public issue trackers.
- MEDIUM: intermittent Amazon-side routing incidents exist (one captured forum snippet; thread now unreachable, could not read the full Amazon acknowledgment).
- LOW / WITHDRAWN: our "30-second settling window after one-shot play" hypothesis. It fits our 3 points but has zero external corroboration, and the docs' unconditional wording argues against it. It may equally be default-music competition (probabilistic per utterance) or a transient platform incident.

## Implications for JF-392

1. Correct the task conclusion: not "platform timing behavior (settled)" but "intermittent stop-routing failure; mechanism unconfirmed; candidate mechanisms: (a) default-music NLU competition (verified class), (b) transient Amazon routing incidents (documented class), (c) timing/settling (unsupported)".
2. The README FAQ entry asserting the 30s window should be softened to "intermittent platform routing; pause always works".
3. If we want a real answer, the discriminator is data, not research: instrument the skill to log, for every playback start, (invocation mode, time-since-start) alongside stop-attempt outcomes, and collect N>20 instances. If failures cluster under 30s of one-shot starts, the timing hypothesis earns MEDIUM confidence; if they scatter randomly, competition/incident is more likely.

## Sources

1. Long-form audio guide, developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html. Contains the PauseIntent routing note ("was the skill most recently playing audio").
2. AudioPlayer interface reference, developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html. Play/shouldEndSession interaction; no settling window documented.
3. alexa-samples/skill-sample-nodejs-audio-player issue #58. Maintainer (sebsto) statement on stop/next/prev forwarding, unconditional.
4. alexa-samples/skill-sample-nodejs-audio-player PR #158. StopIntent delivered to last-playing skill even after playback stops.
5. Amazon developer forums (answerhub, offline) thread a78813 "AMAZON.PauseIntent Not Working". Amazon-acknowledged "known issue" of PauseIntent not reaching skills; snippet captured via search.
6. Stack Overflow question 49188601 "Alexa not stopping audio playing in custom skill". Stop vs Pause handling during playback (community).
7. My Media for Alexa voice commands doc, docs.bizmodeller.com/my-media-for-alexa/voice-commands.html. Plain "Alexa, stop" documented, no timing caveat.
8. alams154/music-assistant-alexa-skill-prototype. Analogous self-hosted music skill; no stop-routing timing issue in tracker.
9. GitHub issue searches across alexa/alexa-skills-kit-sdk-for-nodejs, alexa/alexa-skills-kit-sdk-for-python, alexa-samples. No matching reports.
