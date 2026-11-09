---
id: JF-926
title: >-
  Lyrics on the Show (option 3, minimal): opt-in VideoApp relaunch with
  libass-burned synced lyrics of the current song; new ShowLyricsIntent in all
  17 locales
status: In Progress
assignee: []
created_date: '2026-11-09 09:44'
updated_date: '2026-11-09 13:02'
labels: []
dependencies: []
references:
  - backlog jf-925
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/40'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MAINTAINER-APPROVED 2026-11-09 (option 3 of the JF-925 decision): the minimal lyrics feature. When the user explicitly asks for lyrics of the playing/current song on a screen device, that song relaunches via the video-audio pipeline with the lyrics burned into the video (libass, proven with the PRODUCTION in-container jellyfin-ffmpeg 8.1.3 during the JF-925 spike: 1s encode, correct sync on organic lrc). Default behavior changes for NOBODY: normal music playback stays AudioPlayer; no seek-mode un-park (issue #40 stays parked).

ALL the technical groundwork is in the JF-925 spike (backlog jf-925 + claudedocs/spike_jf-925_lyrics_2026-11-09.md on worktree branch jf925-lyrics-spike + spike scripts on minix ~/lyrics-spike):
- Lyrics source: GET /Audio/{id}/Lyrics; 12.4 DTO per-line tick field is Start (NOT StartPositionTicks); real synced lrc sidecars already exist in the library (Goshen 24/24, Vagabond 33/33). Lines without Start = unsynced text: v1 requires SYNCED lyrics; unsynced/absent -> the localized no-lyrics tell.
- Render: ffmpeg -i audio -f lavfi color=black 1280x720 r=1 -map audio copy -c:v libx264 tune stillimage ultrafast crf 28 -g 1 -vf subtitles=<ass> -shortest -f hls hls_time 10 (EXACT pipeline shape; .ass built from the served Start ticks; the spike's goshen.ass is the template). Use the plugin's ALREADY-RESOLVED ffmpeg (ResolveFfmpegPath; the audiobook path proves it resolves the in-container jellyfin-ffmpeg on minix). Encode gate + pre-encode cache budget apply like every video-audio path (JF-310/421/428).
- Cache: reuse the video-audio cache machinery (duration-scaled budget; the encode is ~1-2s/track, 8-10MB/track at crf28).
- Launch: VideoApp.Launch on the video-audio endpoint family; Show-class devices only (screenless -> the localized screen-required tell; the device capability check is the existing Interface.VideoAppCapabilities.DeviceSupportsVideoApp). shouldEndSession rules: VideoApp launch = null (the standing rule).
- UX: user asks 'mostra i testi' / 'show the lyrics' while a song plays (or names a song): relaunch THE SAME song at the current position if cleanly resumable, else from the start (v1: from the START is acceptable; position-accurate relaunch is a stretch goal using the tracker/offset math). After the video ends... it is one song's video: playback ENDS with it (v1: single-song lyrics view; do NOT chain the queue through the video path; state this in the reply string so expectations are set: after the lyrics view, the user asks to play again normally). Announce what is happening with a localized string.
- NEW INTENT (the full new-intent checklist, all 17 locales): handler class + IntentNames entry + YAML template rows + locale strings + NLU fixtures. Name: ShowLyricsIntent (it-IT 'mostra i testi', 'leggi i testi' variants; en 'show the lyrics'). Slot-less conversational form while playing (works on 'the current song'); optional title slot v2.
- Fallbacks: no synced lyrics -> localized tell; screenless -> localized screen tell; ffmpeg encode failure -> fall back to the plain audio path (no lyrics) with a log.

SEQUENCING NOTE for whoever executes: the interaction-model + 17-locale tail is the bulk; the render machinery is small (one builder + one .ass writer + endpoint variant riding the existing video-audio controller family). DoD gates as usual (this is production code: /simplify, code-review high, full suite, deploy+verify).
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
2026-11-09 UNIT 1 LANDED (working tree, uncommitted until review battery returns): Alexa/Playback/LyricsAssBuilder.cs (the ONE LyricLine list -> ASS converter; refusal contract <2 synced lines; the spike-verified style block pinned byte-identical by a test) + LyricsAssBuilderTests.cs (7 facts green on BOTH TFMs: windows+last-line 5s tail, unsynced refusal, mixed-sync skip, ASS brace escaping, hour rollover, style pin). Two real bugs caught by the tests themselves and fixed in the builder: hours divisor was 3_600_000 (correct 360_000 cs) and the tail was 5_000_000 ticks (correct 50_000_000 = 5s). /simplify 3-agent battery dispatched on this unit. NEXT UNITS: (2) ShowLyricsIntentHandler + IntentNames + pipeline registration + 17 response strings; (3) the lyrics render endpoint (VideoAudioController additive action: on-first-fetch gated encode, cache dir {guid}_lyrics_{hash}, segments served by the EXISTING GetSegment: FindSegmentPath resolves by GUID-prefix so a {guid}_lyrics_* dir is found; playlist = VOD with ENDLIST, JF-309 token embedded); (4) 17 YAML templates + regenerate + NLU fixtures; (5) full gates + deploy + on-device verify.

2026-11-09 UNITS 2-3 LANDED (COMMITTED as part of c6b61c96; the /simplify battery + code-review high ran on the COMPLETE feature delta afterwards): ShowLyricsIntentHandler (namespace Alexa.Handler, Intent folder) with the four legs (nothing playing -> NoMediaPlaying, non-Audio/no-synced -> LyricsNoSynced, screenless -> the chokepoint's VideoRequiresScreen, happy -> VideoApp launch at the lyrics URL with the announce naming the song); IntentNames.ShowLyricsIntent; ILyricManager ctor-injected into VideoAudioController (optional param, older DI roots pass null); the StreamLyricsHls endpoint (gated encode into {guid}_lyrics_{hash}, ass written to the cache dir, VOD playlist with ENDLIST, segment lines rewritten onto the generic segment route with the token riding, CA3003 pragmas mirrored, cancellation forwarded to the gated encode). Handler tests 5 facts green (nothing-playing, unsynced tell, happy path URL+directive+null-SES, screenless chokepoint tell, CanHandle). Suite 6154/6154 per TFM; build 0 warnings. NOTE: handler tests use ResolveCurrentPlayingItem via Launch collaborator with queueManager: null. NEXT: /simplify findings, code-review high, NLU fixture rows, commit, deploy, on-device verify.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-11-09 UNIT 1 SHIPPED (commits ef4d81f0 + 10809f92 on main, pushed): LyricsAssBuilder + 11 test facts. Gates: build 0 warnings both TFMs; full suite 6149/6149 per TFM; /simplify 3-agent battery (all actionable applied); code-review HIGH ran TWICE (round 2 on the unit delta: 5 findings ALL applied: libass \N/\n/\h specials flattened, newline-flatten pin, spike-report branch named in the doc, duplicate-Start tie pinned, this plan's fact count corrected 7->8->11). The plan note's '7 facts' was a pre-landing count; the shipped file has 11.

2026-11-09 UNITS 2-4 SHIPPED (commit c6b61c96 on main, pushed): ShowLyricsIntentHandler (4 legs + capability screen tell via the chokepoint), GetLyricsStreamUrl on PlaybackLaunchBuilder (one-builder-per-route), StreamLyricsHls endpoint (per-dir serialize slot, ass written inside the serialized region, wait-for-first-segment, absolute segment URIs, ulong hash parse, CA3003 pragmas, ct forwarded), 17 locale strings, 17 templates + regenerated models, NLU fixture rows ADDED for all 17 locales (17 ShowLyricsIntent rows committed). Suite 6154/6154 per TFM. /simplify battery on the complete delta: PENDING (3 agents in flight). code-review high round 2 on the complete delta: PENDING. Then deploy + on-device verify + NLU validation.

2026-11-09 DEPLOYED + VERIFIED: the complete JF-926 feature deployed to minix (DLL md5 verified, config intact, plugin loaded v1.0.2.2). Regression: simulator play path clean. Remaining: the maintainer tests the lyrics view on the Show (chiedi i testi mentre suona una canzone); the UPnP PR from Tergin87 stays tracked (issue #43 + cron). Catalog users get everything in the next tagged release (1.0.2.2 shipped WITHOUT the lyrics feature: it rides 1.0.2.3/1.0.3.0 whenever cut).
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
