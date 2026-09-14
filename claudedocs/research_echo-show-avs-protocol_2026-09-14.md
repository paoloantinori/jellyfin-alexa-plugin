# Research Report: Echo Show AVS protocol and what Amazon Music gets that a custom skill cannot

**Date**: 2026-09-14
**Depth**: exhaustive (3 parallel sweeps: official AVS/ASK docs, unofficial community captures/reverse engineering, first-party gating)
**Confidence**: HIGH on the gating conclusions (official docs plus our own device/console captures agree); MEDIUM on one lead worth an experiment (Alexa.SeekController reachability)

## Executive Summary

The protocol a real Echo Show speaks is AVS over HTTP/2 (multipart MIME JSON plus audio, a long-lived downchannel, per-device certificates), and everything Amazon Music gets that we lack comes from two gated surfaces: the Music/Radio/Podcast Skill API (server-driven playback, queue, seek, native transport) and the device-side RenderPlayerInfo/TemplateRuntime stack (the seek bar and full transport card live there, never on the skill path). One genuine surprise: the Music Skill API is now self-serve for MUSIC skills in the US per the live docs ("Anyone can build a music skill for public distribution in the United States"), which changes the 2023-era "partnership only" belief, though locale coverage (it-IT), Lambda hosting, and catalog-feed requirements still gate practical use. Separately, `Alexa.SeekController` exists as a skill-implementable device API with a pre-built voice model and is the one unexplored lead for seek-like behavior inside our current architecture.

## Findings

### 1. The wire protocol (documented, archived; the live AVS docs are retired)

- AVS clients speak HTTP/2 with multipart MIME messages: events POST to `/v20160207/events`, directives arrive on a GET downchannel `/v20160207/directives` opened within 10s of connect, held half-closed from the device and open from AVS for the connection's life; `SynchronizeState` (System) must follow connect; max 10 concurrent streams; PING every 5 min idle. Auth via LWA token. [1][2][3][4]
- Capability interfaces frame everything: cloud sends directives, device sends events, state reported as context. [4]

### 2. Media interfaces on the device side (what the Echo Show hardware knows)

- AudioPlayer (device side): Play/Stop/ClearQueue directives; PlaybackStarted/Stopped/Finished/NearlyFinished/Failed events; mandatory state machine; every event carries playerActivity and offset. This is the device mirror of the skill-side interface we already use. [5]
- PlaybackController (device side): button/GUI press events only (Play/Pause/Next/PreviousCommandIssued v1.0; Button/ToggleCommandIssued v1.1). Confirms our reference: it is never a voice path. [6]
- TemplateRuntime: RenderTemplate and **RenderPlayerInfo** directives drive the on-screen player card; RenderPlayerInfo must track the Play sequence. Its payload includes content (title/subtitle/length/art), provider name/logo, and a `controls` array: `PLAY_PAUSE, NEXT, PREVIOUS, SKIP_FORWARD, SKIP_BACKWARD, SHUFFLE, LOOP, THUMBS_UP, THUMBS_DOWN`; the full transport lives HERE, on the device-maker path. "Navigation controls vary by content provider." No seek slider in the 2016/2019 payload. [7][8]
- The smart-screen SDK (Echo Show's stack) combines APL RenderDocument with TemplateRuntime RenderPlayerInfo; its release notes even mention fixing "RenderPlayerInfo card play/pause/right/left buttons not working". [9]

### 3. What a custom skill gets (our side, documented)

- Exactly Play/Stop/ClearQueue plus the five playback events; `offsetInMilliseconds` only sets the START position; no seek directive exists anywhere on this path. [10]
- Screen metadata only via `audioItem.metadata` (title/subtitle/art/background); tap controls limited to next/previous/pause/play (arriving as PlaybackController requests); no scrubber is listed, and no doc sentence explicitly says "custom skills cannot seek": the absence is structural. Built-in transport intents route to the LAST audio skill only until the user does other audio actions; self-defined intents always need the invocation name. Play responses must set `shouldEndSession=true`. [10][11]
- **Official doc CONFIRMS our console capture**: "When your skill isn't in an active session but is playing audio, or was the skill most recently playing audio, utterances, such as 'Alexa, stop.' cause Alexa to send the `AMAZON.PauseIntent` instead of the `AMAZON.StopIntent`." [11] Our device evidence (bare stop reaching neither us nor stopping playback when Amazon Music competes) goes FURTHER than the docs and remains undocumented platform behavior: no official page describes the default-music-service claim during custom-skill playback. Our live captures stand as the record.

### 4. The first-party gap, precisely

- RenderPlayerInfo/TemplateRuntime: device-makers only; no skill-side API exists to send it (structural: no skill docs define one). [7][10]
- Music/Radio/Podcast Skill API: server-driven model (Alexa.Media.Search GetPlayableContent, Alexa.Media.Playback Initiate/Reinitiate with cross-device playbackSnapshot, PlayQueue GetItem; voice GetNextItem/GetPreviousItem/JumpToItem, SetShuffle/SetLoop/SetRepeat; GetView for graphics). Platform-built voice model; catalog feed (e.g. weekly); streaming rights; Lambda hosting. Seek: the components reference documents SEEK_POSITION/ADJUST controls; the overview sections scanned do not show an in-track seek interface (unclear). [12][13]
- **Access changed**: live docs state "Anyone can build a music skill for public distribution in the United States. However, radio and podcast skills are currently in developer preview." The console offers a "Music" model. The overview page still carries the older "representative must invite you" wording for the general case, an unresolved doc-internal inconsistency. Radio kit (RSK) went self-serve GA in Apr 2023. [14][15][16]
- Default music provider: the Alexa app's Default Services list contains only LINKED provider accounts (Amazon Music, Spotify, Apple, TuneIn, Deezer, SiriusXM, Tidal...); no official doc says a custom AudioPlayer skill can appear there. [17]

### 5. Unofficial captures / reverse engineering

- The cloud CONTROL channel behind Amazon Music on Echo is the logged-in-account web API, not AVS: `/api/cloudplayer/queue-and-play` (plus gotham/prime-playlist variants), queue state via `/api/np/player` and `/api/media/state`, transport via `/api/np/command` (Pause/Play/Next/Previous/Volume/ShuffleCommand). The general trigger is `POST /api/behaviors/preview` with `Alexa.Music.PlaySearchPhrase` (musicProviderId AMAZON_MUSIC); a server-side validate call sanitizes the phrase. Maintained artifact: adn77/alexa-remote-control (v0.23, 2025-11); alexapy/alexa_media_player (Home Assistant) implement the same surface. [18][19][20]
- A developer analysis (navidrome-alexa ADR001) tried injecting a custom skill id into the entertainment/queue token path and FAILED: third-party skills cannot ride the first-party queue channel. [21]
- No public dump exists of first-party Amazon Music directives on an Echo Show; no successful MITM writeup of Echo traffic exists at all (10 years of hardware; AVS device auth uses per-device X.509 client certificates, which structurally defeats passive MITM). The only real device-side capture format visible to developers is the `SkillDebugger.CaptureDebuggingInfo` dump, exactly what we collected today. [22]
- Device stream formats (device-maker doc, 2019): Amazon Music serves HLSv4 MPEG-TS AAC 256kbps; device makers must support HLS v7 live/VOD, PLS, M3U, AAC LC/HE-AAC, MP3. [23]

### 6. Actionable leads for this plugin

1. **Alexa.SeekController** (device-apis): skill-implementable, pre-built voice model ("Alexa, skip thirty seconds on device"), for "devices and services that can seek to a specific position". Whether a custom skill playing via AudioPlayer can ALSO implement it (and whether the directives arrive) is untested and undocumented in our sources; it is the only lead for seek-like behavior without MSAPI. Cheap experiment: declare it, log the directives. [24]
2. **MSAPI self-serve (US)**: a future "Jellyfin Music skill" could in principle get the native transport/queue model, but practical gates today: US-public wording (locale support for it-IT unverified), Lambda hosting, catalog feed, streaming-rights self-certification; a separate project, not a patch. [14]
3. Our live captures (device and console) are the best evidence class available for the routing limits; keep collecting them (they matched the official PauseIntent-vs-StopIntent doc exactly).

## Confidence Assessment

- HIGH: the AVS wire protocol, device-side media interfaces, RenderPlayerInfo control model, custom-skill AudioPlayer limits, the official stop-to-PauseIntent substitution rule, the web-API control channel behind Amazon Music (verified-in-repo code), the absence of public first-party directive dumps and successful Echo MITM.
- MEDIUM: MSAPI self-serve reach in practice (live official wording vs older invitation wording conflict; US-only; locale coverage unverified); "no seek on custom skills" (structural absence, no explicit sentence).
- LOW/unresolved: Alexa.SeekController reachability for a custom skill (untested); Amazon Music stream DRM specifics (nothing public found).

## Sources

1. https://web.archive.org/web/20191021223125/https://developer.amazon.com/docs/alexa-voice-service/structure-http2-request.html
2. https://web.archive.org/web/20191021223125/https://developer.amazon.com/docs/alexa-voice-service/manage-http2-connection.html
3. https://web.archive.org/web/20200103132649/https://developer.amazon.com/en-US/docs/alexa/alexa-voice-service/api-overview.html
4. same as 3 (capability-interfaces framing)
5. https://web.archive.org/web/20191021195952/https://developer.amazon.com/docs/alexa-voice-service/audioplayer.html
6. https://web.archive.org/web/20191021195952/https://developer.amazon.com/docs/alexa-voice-service/playbackcontroller.html
7. https://web.archive.org/web/20191022000739/https://developer.amazon.com/docs/alexa-voice-service/templateruntime.html
8. https://github.com/alexa/alexa-smart-screen-sdk/releases
9. same as 8
10. https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html
11. https://developer.amazon.com/en-US/docs/alexa/custom-skills/use-long-form-audio.html
12. https://developer.amazon.com/en-US/docs/alexa/music-skills/api-components-reference.html
13. https://developer.amazon.com/en-US/docs/alexa/music-skills/api-reference-overview.html
14. https://developer.amazon.com/en-US/docs/alexa/music-skills/understand-the-music-skill-api.html
15. https://developer.amazon.com/en-US/docs/alexa/music-skills/steps-to-create-a-music-skill.html
16. https://developer.amazon.com/en-US/blogs/alexa/alexa-skills-kit/2023/04/alexa-skills-radio-kit-march
17. https://www.dummies.com/article/how-to-listen-to-music-on-amazon-alexa-260972/ (plus Sonos community thread 6855635)
18. https://github.com/adn77/alexa-remote-control/blob/master/alexa_remote_control.sh
19. https://pypi.org/project/alexapy/
20. https://github.com/alandtse/alexa_media_player
21. https://github.com/Ahimgit/navidrome-alexa/blob/main/doc/ADR001-Alexa-Interactions.md
22. https://github.com/fitchMitch/alexa_test/blob/master/debug.json (a real SkillDebugger capture)
23. https://web.archive.org/web/20191021023443/https://developer.amazon.com/docs/alexa-voice-service/music-service-providers.html
24. https://developer.amazon.com/en-US/docs/alexa/device-apis/alexa-seekcontroller.html
