---
id: JF-625
title: >-
  JF-625 - Music seek mode: the audiobook video-audio pipeline (real seek bar)
  applied to music, spike-first behind a flag
status: To Do
assignee: []
created_date: '2026-09-24 16:16'
updated_date: '2026-09-24 21:18'
labels: []
milestone: 1.0 polish
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Born from the JF-624 round-8 verdict exchange (2026-09-24): Paolo challenged the 'loses queue/radio/gapless/cover' dismissal and the challenge was CORRECT - those were unbuilt features, not intrinsic losses. The one intrinsic constraint is that VideoApp emits no playback events to the skill (position tracking via segment fetches already exists: AudiobookPositionTracker). Goal: music playback with a REAL seek bar on the Echo Show via the existing audiobook video-audio HLS pipeline (1fps stillimage video + audio copy), instead of the AudioPlayer path where the native surface has no scrubber for custom skills (MSAPI-only) and covers any APL document.

Plan (spike-first, behind a new flag default OFF, e.g. MusicSeekMode):
1. SPIKE: single song via video-audio with COVER ART as the video track (-loop 1 -i cover.jpg + scale/pad to 1280x720, -tune stillimage, -g 1, 10s segments) instead of the black frame; verify on the live Show: playback, cover visible, seek bar present and functional (seek across the track), no black screen.
2. Queue-as-concat: album/playlist = one concat stream (the audiobook chapter shape with tracks), per-track offsets known server-side for MediaInfo/resume.
3. Radio extension: event playlist without ENDLIST; server appends next radio track's segments before exhaustion (AutoPopulate logic moves server-side).
4. Resume via the existing segment-based position tracking + sliced playlist (?start=).
5. Keep AudioPlayer as the default path; the mode is per-user opt-in (config flag), so nothing in the critical path changes.

Known costs to state in the config UI: a few seconds of first-play encode latency; voice transport during playback unchanged (no worse than today). Track title/artist can be baked into the video frame later (polish).
<!-- SECTION:DESCRIPTION:END -->

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

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 [ ] SPIKE gate: one song plays via video-audio with the album cover as the video track on the live Show: audio correct, cover visible, seek bar PRESENT and a mid-track seek lands correctly, no black screen
- [ ] #2 [ ] Flag MusicSeekMode (default OFF) gates the whole path; flag off = byte-identical AudioPlayer behavior
- [ ] #3 [ ] Position tracking works via segment fetches (AudiobookPositionTracker shape) and MediaInfo answers the right track/position
- [ ] #4 [ ] Queue-as-concat: an album plays start to finish as one stream with correct per-track reporting
- [ ] #5 [ ] Radio extension: the event playlist appends segments before exhaustion and playback continues seamlessly
- [ ] #6 [ ] First-play latency measured and documented (encode start to audio out)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-24 SPIKE REDUCTION TO ZERO CODE: while wiring a new MusicSeekMode flag I discovered the feature ALREADY EXISTS END TO END - PluginConfiguration.NativeControlsForAudio (global) + User.VideoAppForAudio (per-user) + GetVideoAppForAudio resolver + the routing site inside BuildAudioPlayerResponse (item is Audio -> BuildVideoAppAudioResponse, with the JF-505 screenless degrade). The video-audio controller's single-item path resolves the album cover as the video track (useBlackFrame only as fallback), audio-copy for mp3/aac, encode gate + prewritten playlist (JF-536) all included. My duplicate flag/helper/test were reverted uncommitted. Spike = flip the EXISTING flag on minix (done 2026-09-24, PATCH verified NativeControlsForAudio: true) + device verification. The stale flag doc ('without album art') was corrected. AWAITING DEVICE TEST: one song play must show cover as video, native player with REAL seek bar, a mid-track seek, correct audio; then the queue/radio-extension steps (plan items 2-3) evaluate against what the launch path already does.

2026-09-24 19:02 SPIKE GATE PASSED (device-verified by Paolo): 'stavolta ha funzionato tutto! ha perfino ripreso la riproduzione precedente. e barra funzionante!' One mid-flight bug found and fixed on the way: the first attempt died at prefetch (green frame, cover video, no audio/controls) because the JF-536 pre-written listing CEIL-estimates the segment count (68) vs ffmpeg's 67; a 4-min song prefetched the whole stream in ~1s, requested the promised-but-missing seg_067.ts, 404 x3, player fatal before playback start. Fix: skip the pre-write for content <= 10 min (PrewriteListingMinRuntimeTicks) - the encode completes to ENDLIST before the device's first fetch; long content keeps the pre-write (JF-531 live-edge guard; the tail reload heals the count there). Poisoned cache entry cleared; replay encodes fresh. Wire evidence 19:02:30-:38: per-user VideoAppForAudio=true honored, 'short content (4.5 min), skipping the pre-written listing' log line, 67 segments, 0 ffmpeg errors, cached playlist served on refetch. The 'resumed previous playback' Paolo saw = the JF-617/619 resume offer machinery working on the VideoApp route (RecordLastPlayed via the JF-563 ledger site; the song itself started at 0, judged 96% played). KNOWN EXPECTED GAP for the next build step: an ALBUM/queue play launches the FIRST track via VideoApp and nothing advances at the end (VideoApp emits no playback events) - queue-as-concat (plan item 2) is what fixes it; warn users testing single songs only until then. Also noted, unfixed: the announce TTS can be cut by the player opening (the JF-501 progressive-response vehicle exists for the video paths; verify whether the music announce rides it on this route).

2026-09-24 night autonomous pass (Paolo off-device until tomorrow): (1) web-simulator check of the progressive announce was INCONCLUSIVE-BY-DESIGN: the simulator's device profile carries no VideoApp interface, so the JF-505 capability gate correctly degraded the album play to a plain AudioPlayer response (verified in the response body corr=e55af082 at 21:05) - the vehicle only fires on screen profiles; the check DID re-confirm the album routing works and the device log shows the searching progressive send. The progressive album announce needs tomorrow's Echo round; pin it with a unit test first (capture sendProgressiveResponse + VideoApp context, assert the album name rides the vehicle). (2) /simplify dispatched over 39c8ba17..HEAD (4 angles, background); /code-review high follows after findings are applied; the DoD gates the deploy hook keeps demanding get burned down tonight. (3) NEXT-TRACK BY VOICE (the next/prev platform limit compensation, Paolo offered the direction): a one-shot 'chiedi a mia collezione di saltare la canzone' re-launches the album concat at the NEXT track's offset - the server knows the per-track offsets; needs the device queue/ledger to know which album stream is live. Scoped as a follow-up item, not started.

2026-09-24 night /simplify PASS COMPLETE (4 agents, all findings applied, commit f289a60a, suite 4260x2, deployed): (1) AlbumTrackOrder shared constant replaces the inline OrderBy (drift risk vs the resume-offset math); (2) the album announce composes POST-HOC on the observed route (directive present + attached speech + request) instead of re-deriving the builder's gates, GetAnnounceAudioPlays back to private; (3) albums skip the pre-written listing ENTIRELY and the concurrent-encode guard serves the live listing for album keys (was: prewrite written-but-unserved on first fetch yet SERVED on the concurrent path, the dead-player shape surviving in a race window); (4) ShouldPrewriteFullListing shared helper now gates the EPISODE prewrite too (was ungated, same phantom-tail exposure for short video items); (5) the YesIntent album-confirm and the APL carousel folder-tap pass collectionParentId (same whole-album seek bar as the voice ask; MusicAlbum only, JF-361 single-file books excluded); (6) test/type minor cleanups. Skipped: the collection-profile object (when a third collection type arrives). code-review high dispatched on the same range (background fork). STILL OPEN for tomorrow's device round: progressive album announce verification (needs a VideoApp profile), whole-album play after the simplify pass (behavior-preserving but live-verified anyway), the next/prev platform-limit answer (voice-skip follow-up scoped).

2026-09-24 night code-review HIGH PASS COMPLETE (forked skill run; 10 findings: 8 applied, 1 documented choice, 1 filed; commit 46143d89, suite 4261x2, locales PASS, deployed): (1) consistent cold ?start= drop on every album serve path; (2) AlbumIds fallback in the endpoint (split albums no longer 404 the launched URL); (3) codec-uniformity check on album concats (AAC transcode when mixed; the reviewer EMPIRICALLY VERIFIED with ffmpeg 8.1.2 that one M4A among MP3s silently truncates the album under -c:a copy, exit 0); (4) episode prewrite gate REVERTED (my own simplify regression, live-edge join for short episodes); (5) PlayingMedium.VideoAppAudio + CannotNavigateMusicByVoice in 17 locales (one-shot next/prev over a VideoApp music stream now refuses honestly instead of double audio); (6) art-tuned cache headroom 192MB/h (measured); (7) controlled 500 on failed first segment; (8) announce swap AFTER ApplyAnnouncement + request threaded through the JF-345 cascade. DOCUMENTED CHOICE: APL album tap = fresh listen from 0 (voice ask resumes). FILED for tomorrow: album progress persistence on the VideoApp route (acceptance criterion #3: FindResumeTrackIndex reads state the route never writes; needs an album-keyed tracker reader), and the cold-encode 503 window for album keys. ALSO: the simplify commit's AlbumTrackOrder claim had NOT applied (first patch script aborted pre-write; the reviewer caught the gap) - applied for real now. Tomorrow's device battery: whole album + announce-by-vehicle + the refusal line on one-shot next + cold resume-from-mid-album.

2026-09-25 ~00:00 night session final: (1) the three AlbumAnnounceVehicleTests pinned the vehicle composition AND caught a real regression from the review pass (the swap-after-ApplyAnnouncement had reverted the vehicle speech to the TRACK name; now: the caller's announcement when applied, else the ALBUM name; success=speechless final response, failure=speech rides it). (2) Criterion 3 SERVER SIDE DONE: seek-mode album resume reads the AudiobookPositionTracker (album-GUID keyed; VideoApp emits no events so UserData never moves on the route), maps the absolute position onto (resume track + in-track partial), the partial rides collectionStartTicks; pinned by SeekModeAlbumResume_TrackerPosition_MapsOntoTrackAndOffset. Device half of criterion 3 still open (MediaInfo position display during playback). (3) Night e2e battery (it-IT + artist matrix, 28 ran): it-IT 4/4 PASSED after the whole night's 25-file diff - the regression net is clean; the 14 failures are ALL the open-step 'unexpected error' simulate outage signature (JF-551 class; en locales also degraded tonight, flaky per-call). (4) es-ES outage cron continues. TOMORROW'S BATTERY (unchanged + additions): whole album + full announce; one-shot next -> refusal line; album re-play after stopping mid-album (tracker resume: should start at the stopped TRACK, warm cache mid-track); single song; criterion-3 MediaInfo check ('cosa sta suonando' during album playback). Remaining build items: playlists as collections (third collection type - the profile-object refactor trigger), radio stream extension, voice-skip.
<!-- SECTION:NOTES:END -->
