# Research Report: How to honor the stop protocol for audio (AudioPlayer) and where audio streams fall in the category map

**Date**: 2026-09-07
**Depth**: exhaustive
**Confidence**: HIGH on the documented protocol (official Amazon docs, current); MEDIUM on the observed divergence (single on-device session, wording confound open).

## Executive Summary

For AudioPlayer playback the platform documents a real transport protocol, and it is broader than our live evidence suggested: while a skill is playing audio, or was the most recent skill to play audio, users can invoke Pause, Resume, Next, Previous, StartOver, Repeat, and Cancel WITHOUT the invocation name, and Next/Previous/StartOver/Repeat are delivered even when absent from the intent schema. Stop is the documented exception: it is not in the no-invocation-name list at all; it is the exit-skill intent, and honoring it means responding with shouldEndSession true or null plus, when audio is ours, the AudioPlayer.Stop directive. Our plugin already implements the full documented set. The one open item is a divergence: the docs promise next/previous routing that our 2026-07-02 device test did not observe, and that test used "avanti", which may not be a canonical it-IT NextIntent word ("successivo" is); a re-test with documented words is queued. Audio streams split into exactly two categories: everything sent via AudioPlayer.Play (music, episode audio transcode from JF-507, audiobooks with native controls off) has the full stop protocol available; everything sent via VideoApp.Launch (video, audiobooks with native controls on) has no stop directive at all and only the 30-second session window (see research_alexa-videoapp-stop-routing_2026-09-07.md).

## Findings

### 1. Which intents route to the skill during AudioPlayer playback (documented)

From the Standard Built-in Intents table (developer.amazon.com, verified 2026-09-07). Each of these rows carries the same clause: "Users can invoke this intent without using your invocation name if your skill is currently playing audio or was the most recent skill to play audio":

- `AMAZON.PauseIntent`: must be implemented by any AudioPlayer skill.
- `AMAZON.ResumeIntent`: must be implemented by any AudioPlayer skill.
- `AMAZON.NextIntent`: "The intent is sent to your skill in this case even if AMAZON.NextIntent is not in your intent schema."
- `AMAZON.PreviousIntent`: same schema-independent clause.
- `AMAZON.StartOverIntent`: same clause.
- `AMAZON.RepeatIntent`: same clause.
- `AMAZON.CancelIntent`: also carries the no-invocation-name clause during playback.
- `AMAZON.StopIntent`: NOT in the list. "Exits the skill. Your skill must implement this intent and shouldEndSession must be true or null in the response", flagged as a certification requirement (see certification requirements for stopping and canceling).

The "most recent skill to play audio" clause also covers the after-playback case: a bare "Alexa, stop"/"next" shortly after our stream ended should still route to us, not only mid-playback.

### 2. The honoring contract (documented)

- Stopping our audio: `AudioPlayer.Stop` directive, or `AudioPlayer.ClearQueue` with `CLEAR_ALL` (stops the current stream and empties the queue; `CLEAR_ENQUEUED` keeps the current stream). One `Play` directive per response maximum.
- Play responses: "set the shouldEndSession flag in the response object to true to end the session. If you set this flag to false, Alexa sends the stream to the device for playback, and then pauses the stream to listen for the user's response." This is the documented mechanism behind our JF-299 rule (play responses must be session-ending).
- Voice interactions during playback: "PlaybackStopped... This request is also sent if the user makes a voice request to Alexa, since this temporarily pauses the playback. In this case, the playback begins automatically once the voice interaction is complete." So PlaybackStopped does not imply a terminal stop; the skill must not treat it as one.
- Event responses: no response required for AudioPlayer requests; if we respond, keep-alive ack (shouldEndSession null) or Play; never false (the JF-299 InvalidResponse rule).
- Touch controls on screen devices during audio: the pause tap stops playback natively and sends NO request to the skill; next/previous/play taps send `PlaybackController.NextCommandIssued` / `PreviousCommandIssued` / `PlayCommandIssued` (and PauseCommandIssued exists for hardware remotes). Queue progression: respond to `PlaybackNearlyFinished` with `ENQUEUE` or `REPLACE_ENQUEUED` Play directives; `expectedPreviousToken` guards against out-of-order directives (mismatch causes the device to ignore the directive).

### 3. Plugin readiness (code-verified)

We already implement the full documented transport set: `NextIntentHandler`, `PreviousIntentHandler`, `PauseIntentHandler`, `ResumeIntentHandler`, `StartOverIntentHandler`, `LoopOn/OffIntentHandler`, `ShuffleOn/OffIntentHandler`; stop/cancel are handled by `PauseIntentHandler` (log-verified 2026-09-07: "isStopOrCancel=True ... ending session with AudioPlayer.Stop"). `PlaybackControllerRequest` types are routed in the Play/Pause/Previous/Next/Resume handlers. Nothing in the documented protocol is unhandled.

### 4. The divergence and its confound

The docs promise unconditional next/previous/startover/repeat routing to the active audio skill. Our on-device verification (2026-07-02) recorded zero arrivals for "stop"/"ferma"/"avanti" during AudioPlayer playback, while pause/resume routed as documented, and the simulator showed `ConsideredIntents = <IntentForDifferentSkill>` for the same utterances. Two candidate explanations remain open:

1. Default-music-service NLU competition: the device has a default music service, and some transport words get claimed before the active-skill rule applies (the JF-392 mode-2 classification).
2. Wording: the it-IT canonical NextIntent words are "successivo" and similar; "avanti" may resolve to a different skill's command set (the music service's), which would look exactly like a claim.

The discriminating test is part of the queued device battery: say "Alexa, successivo" (documented word) during skill playback and check the logs for `AMAZON.NextIntent`.

### 5. Category map for our streams

| Stream | Transport | Stop protocol |
|---|---|---|
| Music, songs, playlists | AudioPlayer.Play | Full: AudioPlayer.Stop directive + StopIntent/PauseIntent handling |
| Episode audio-only transcode (JF-507 audio.m3u8) | AudioPlayer.Play | Same as music (it is an AudioPlayer stream; the HLS/transcode nature is invisible to the protocol) |
| Audiobooks, NativeControlsForBooks off | AudioPlayer.Play | Same as music |
| Video (movies/episodes/LiveTV) | VideoApp.Launch | NONE: no VideoApp.Stop exists; only the 30-second session window |
| Audiobooks, NativeControlsForBooks on | VideoApp.Launch (concat HLS) | Same as video |

Server-side note: stopping the DEVICE side of an AudioPlayer stream does not need server teardown; the device stops fetching segments, and the pre-generated encode cache (JF-498 design) is intentional. For raw-static audio there is nothing to tear down at all.

## The verdict on "how to honor stop for audio"

We already honor the documented protocol completely; the gap is not honoring, it is receiving. Stop arrives only as: an explicit StopIntent (in-session, one-shot with invocation name, or in the 30-second window), or never (claimed elsewhere). The one lever still untested is the documented-word re-test (successivo / annulla / stop with wake word during playback), which is in the device battery. If those words DO route, the correct FAQ guidance changes from "use pause" to "use pause, or the documented transport words", and Next/Previous become usable without the invocation name, which would be a real UX win for queue navigation.

## Sources

1. Standard Built-in Intents (routing clauses per intent), developer.amazon.com/en-US/docs/alexa/custom-skills/standard-built-in-intents.html
2. AudioPlayer Interface Reference (directives, PlaybackStopped temporary-pause semantics, queue rules), developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html
3. Stream Long-Form Audio with AudioPlayer (tap controls: pause tap native, PlaybackController for next/prev/play), developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html
4. PlaybackController Interface Reference, developer.amazon.com/en-US/docs/alexa/custom-skills/playback-controller-interface-reference.html
5. Internal: JF-392 (closed; two failure modes), JF-299/JF-387 gotchas, 2026-07-02 on-device verification, 2026-09-07 podman-log timeline, plugin handler inventory (code-verified this session)
6. Related dossier: claudedocs/research_alexa-videoapp-stop-routing_2026-09-07.md (the VideoApp half)
