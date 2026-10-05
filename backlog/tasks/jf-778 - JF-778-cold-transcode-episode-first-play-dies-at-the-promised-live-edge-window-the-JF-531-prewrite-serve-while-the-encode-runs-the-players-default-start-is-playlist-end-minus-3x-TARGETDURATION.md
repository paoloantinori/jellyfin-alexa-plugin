---
id: JF-778
title: >-
  JF-778 - cold transcode-episode first play dies at the promised live edge:
  window the JF-531 prewrite serve while the encode runs (the player's default
  start is playlist-end minus 3x TARGETDURATION)
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-05 14:04'
labels:
  - bug
  - video
  - hls
  - transcoding
  - echoshow
  - device-evidence
dependencies:
  - JF-531
  - JF-503
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-05 from Paolo's LIVE Echo Show device report (filing
reconstructed from the dispatch + the orchestrator's verbatim log paste; the
original heredoc filing was hook-blocked and never landed).

### Device evidence (verbatim, 2026-10-05 12:27, Sailor Moon R E054, mpeg4 source -> transcode tier)

- [12:27:03] Launch: VideoApp.Launch for 'Sailor Moon R - 054 - Il debutto di Rea (by Fil)' (item 39a53265-ec74-2405-dca2-caaf8febcf15), corr=7fc9e477, 501ms response.
- [12:27:05] "VideoAudio HLS cache miss (probed 2 roots, cache root /config/cache/alexaskill-video-audio/39a53265..._639267440373927683 first)" x2.
- [12:27:05] "video codec 'mpeg4' is not known h264; using the video transcode tier (libx264 ultrafast CRF 23, measured 4.40x realtime on the minix 2026-09-08, JF-500)".
- [12:27:05] "transcode for item ... is estimated at 3072MB, under the configured cache cap of 4096MB (VideoAudioCacheSizeMB): the completed encode is cacheable (JF-537)" - NOTE: this episode went to the CACHE root, not the transient root (under cap), so the transient mode is NOT a factor here.
- [12:27:05] ffmpeg args: -hls_time 4, -hls_segment_filename .../seg_%04d.ts (the 4s episode tier).
- [12:27:06] "serving pre-written full listing for item ... (encode in progress, JF-531)" then "HLS resume serve: .../playlist-full.m3u8 served FULL (no position; startTicks=0)".
- [12:27:07/:07/:08] "GetSegment miss: item ... requested seg_0353.ts, highest existing segment 7 [then 7, then 17], activeEncode True, holdEligible False" - THREE requests, the live-edge shape (355 - 2).
- [12:27:44] "Episode HLS encoding complete ...: 355 segments" (~40s).
- Post-encode: playlist-full.m3u8 on disk lists seg_0000..seg_0355 (356 entries, #EXTINF 3.989469 each, NO #EXT-X-ENDLIST, MEDIA-SEQUENCE:0, VERSION:3), dir holds 355 segments + playlist-full + stream.m3u8; a replay serves instantly from cache (the retest worked).

### Mechanism (confirmed against the player source AND the device numbers)

The Echo's ExoPlayer treats the prewrite's no-ENDLIST playlist as LIVE and
resolves its default start position at playlist end minus the live target
offset, whose fallback is 3 x TARGETDURATION (media3
HlsMediaSource.getLiveWindowDefaultStartPositionUs: startOffset else
durationUs + liveEdgeOffsetUs; targetOffsetMs, with getTargetLiveOffsetUs
falling back to 3 x targetDuration absent SERVER-CONTROL; no PROGRAM-DATE-TIME
=> liveEdgeOffsetUs = 0). The device numbers match the arithmetic to the
segment: 356 entries x 3.989469s = 1420.25s; 1420.25 - 12 = 1408.25s; seg_0353
spans [1408.15, 1412.14]. The player FETCHES THERE FIRST. With the encode head
at seg_0007 the file does not exist; the player retries ~3 times within ~1-2s
and dies before the first frame. Second data point of the same family: the
JF-625 song prefetch death requested the promised tail segment (seg_067 of 68)
of a JF-536 prewrite, same no-ENDLIST live-start cause.

Corollary: JF-531's own live verification (2026-09-12, Letterkenny remux) is
reconciled as a warm/complete-encode serve (post-encode the ENDLIST playlist
serves and the default start is 0); a cold encode + full prewrite has the
death shape above whenever the player's first fetch precedes the tail's
existence, which for the transcode tier is the whole encode window.

## Design record: the four candidate shapes, with the failure modes measured

(a) UNBOUNDED FORWARD HOLD during an active encode (extend the JF-503 hold to
any requested > head). THE NUMBER: tonight the wanted segment (seg_0353) was
(353-7)x4s of content away with the encode measured at ~36x realtime
(12:27:05 -> 12:27:44 for 23.7min; the tier's nominal worst case 4.40x gives
~5.4min), so the hold would answer in ~35s (observed) to ~320s (nominal). The
player's tolerance, from the device log, is ~3 fetch attempts within ~1-2s
(requests at :07/:07/:08, then silence): an HTTP request that hangs tens of
seconds hits the player's read timeout and the same 3-attempt death, now with a
parked connection per attempt. DEAD by >10x even in the fast-encode case.

(b) SERVE FFMPEG'S GROWING stream.m3u8 DURING THE ENCODE (drop the prewrite
serve). The listing's edge then tracks the encode head, so the edge fetch
succeeds (existing segment) - but the live default start lands AT THE HEAD:
playback joins mid-content and the seekbar shows the encoded-so-far window.
That is precisely the pre-JF-531 bug this family closed (the Adolescence E2
report: 6-min bar, playback near the end), reintroduced for the whole encode
window. REJECTED as the sole shape; kept as the growth MECHANISM inside (c).

(c) HYBRID: keep the prewrite file as the URL/token source of truth, but SERVE
IT WINDOWED while the encode is live; a time-anchored growing window whose
edge never leaves the encoded region:
K = max(FLOOR=2, min(head + 1, elapsedSegments + LEAD=3)) entries listed,
elapsed anchored on the prewrite file's LastWriteTimeUtc (written per encode
inside the JF-428 pin window, so the anchor is encode start to within ms).
Player math: at the first serve elapsed ~0 => K=2, listing duration 8s, default
start = max(0, 8 - 3x4) = 0: playback starts at the beginning, the edge fetch
hits existing (or JF-503-hold-covered: floor entries beyond the head are within
the hold's +2 lookahead and 3.5s budget, which 4.4x realtime satisfies in
<1s/segment) content. The window then grows one entry per 4s (1x playback) so
the player, playing at 1x, never catches the edge, and the head cap (head+1)
keeps the listing inside the encoded region for stalled encodes. When the
encode completes, the existing cache-hit path serves ffmpeg's ENDLIST playlist
and the player's live refresh lands on the full-runtime VOD (full seekbar, all
segments present). COST, accepted deliberately: the full-runtime seekbar during
the encode window is given up (it is unattainable: any no-ENDLIST listing that
spans the runtime puts the default start at its end; the contradiction that
killed tonight's play), and seeks during the encode are bounded by the grown
window rather than the encoded head. CHOSEN.

(d) EXT-X-START hint (start at 0 on the full listing). Our own hardware
verification refutes it on this player: the audiobook resume StartHint strategy
(full playlist + #EXT-X-START:TIME-OFFSET, PRECISE=YES) was served correctly
and the Echo restarted from 0 - the strategy is dormant in
AudiobookPlaylistBuilder precisely for that reason (the sliced strategy is
active). Upstream media3 would honor the tag (it is the startOffsetUs branch of
the same function), but the device is the authority. DEAD on recorded device
evidence; no new deploy should be built on it.

Also ruled out in the design pass: ENDLIST'd full listing during the encode
(VOD shape): the Echo prefetches aggressively (the JF-625 song prefetched a
4-min stream in ~1s), so any promised-but-missing tail 404s during the initial
buffer fill, unrecoverable at 4.4x (the buffer-ahead outruns the encode by
minutes).

GetSegment hold: UNCHANGED. The JF-503 +2 lookahead / 3.5s budget is exactly
the mechanism that covers the window floor's at-most-2 entries beyond the head
at the first serve, and every later player fetch trails the head (the encode
runs >=4.4x against 1x playback). Widening it is shape (a), killed above.

## Scope and non-goals

Episode path only (TryServePrewrittenEpisodePlaylist is the single serve choke
point: the first serve, the warm-cache own-live row, and via it the transient
root rows). The song twin (TryServePrewrittenVideoAudioPlaylist) and the
audiobook concat prewrite share the death shape in principle (both serve full
no-ENDLIST listings during active encodes) but are NOT touched: the audiobook
path is the daily-driver surface with no reproduced cold-first-play death on
record (its 10s segments + per-segment discontinuities + ~250x copy encode make
the timing picture different), and a cold single-chapter book is rare. Flagged
for the device round: if Paolo's next cold audiobook/long-song first play shows
the same three-404 death, the window applies to those serves as the follow-up.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 #1 While an episode encode is live, the prewrite serve lists only the windowed prefix (floor 2 entries at first serve, growing 1 entry per segment-seconds, capped at head+1); the served listing never contains a segment beyond head+1 unless it is one of the floor's at-most-2 hold-covered entries
- [x] #2 #2 The served listing keeps the event-playlist shape (no ENDLIST, MEDIA-SEQUENCE:0, TARGETDURATION:4) and the token; the prewrite FILE on disk remains the full listing (post-encode and resume-slicing consumers unchanged)
- [x] #3 #3 A resume (?start=) beyond the grown window during the encode drops the offset with a log (the JF-686 still-growing rule); a resume inside the window slices within it
- [x] #4 #4 Red proof: the cold-edge pin (player asks the far-ahead shape while the encode head is at 7, the JF-778 device shape) fails on the unmodified tree and passes after; both TFMs; full suite green
- [x] #5 #5 The JF-503 hold constants and logic unchanged; the JF-680/JF-681/JF-774 prewrite-serve pins stay green (the 2-entry floor covers their short planted listings)
- [x] #6 #6 /simplify + code-review high gates run on the diff
- [ ] #7 #7 Paolo's device round: cold transcode-tier episode first play starts at 0 and does not die; seekbar grows for the encode window then completes; warm replay unchanged (see Final Summary)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
<!-- NOTES:BEGIN -->
2026-10-05: implemented as designed (shape c). TryServePrewrittenEpisodePlaylist
now reads the prewrite once, computes K = max(2, min(head + 1, elapsedSegments +
3)) with elapsed = UtcNow; prewrite.LastWriteTimeUtc and head from the
prewrite's own directory (root-agnostic), truncates the listing to the first K
segment entries when the full listing is longer, and threads the windowed
content through ServeEpisodePlaylistAsync as preloadedContent (one read per
serve, the JF-677/JF-680 read-count funnels preserved). Resume past the window
drops the offset with an Information log (the JF-686 rule); resume inside slices
the windowed content. Constants: EpisodePrewriteWindowFloorSegments=2,
EpisodePrewriteWindowLeadSegments=3 (names + rationale at the definition).
Three JF-531-era pins updated to the windowed first-serve shape (their
"full listing at first serve" assertions described the behavior this task
supersedes; the prewrite-file and ffmpeg-target assertions kept). The core red
pin is StreamHlsEpisode_ColdEdgeEncode_ServesWindowedPrewritePrefix plus the
growth/head-cap, resume-drop, and resume-inside-window pins.

RED RUN (unmodified tree, both TFMs, 2026-10-05): all four new pins failed with
the full-listing serve; ColdEdgeEncode "Expected: 3 / Actual: 356",
GrowsWithElapsedAndCapsAtHead "Expected: 8 / Actual: 356", the two resume pins
failing on the sliced/dropped-shape asserts. Post-fix: 4/4 green both TFMs,
and the full VideoAudioControllerTests class 279/279 both TFMs.

GATES. /simplify (4 parallel agents, reuse/simplification/efficiency/altitude):
applied; the stale GetHighestSegmentNumber hot-path doc, the windowed-predicate
dedupe, the device-narrative consolidation into the method doc, the
PlantLiveEncodeFixture parameterization (adopted by the mid-encode pin, killing
the inline arrange twin), the TruncateToFirstSegments move into
AudiobookPlaylistBuilder next to its slice twin with the ONE IsSegmentUriLine
predicate extraction (was the 4th inline copy), and the
EpisodePrewriteWindowFloor_RespectsSegmentHoldLookahead relation pin. Skipped
with reasons; the two-log merge (the "serving pre-written full listing"
wording is the JF-680/JF-681 branch-attribution contract), the fixed-shape
truncation (bakes the generator's 1:1 EXTINF:URI invariant; the general walk
is 12 lines), the pre-emptive window-helper extraction (the extract-at-second-
copy rule; the twins adoption is the trigger), the span-walk micro-optimization
(consistency with the sibling builders).

code-review high (8 findings): applied 7 - F1 the mid-window resume honor band
(a sliced no-ENDLIST listing joins at slice-end minus 3x TARGETDURATION, so a
resume is only actually honored within LEAD of the window edge; outside the
band the offset now drops, pinned by
StreamHlsEpisode_WindowedPrewrite_MidWindowResume_OutsideHonorBand_DropsOffset),
F2 the 1601-mtime int overflow clamp plus the corrected degrade comment, F3
the JF-531-era comments and the serve log line updated to the windowed truth
(keeping the pin-contract wording prefix), F4 the IsSegmentUriLine doc narrowed
to the URI walks (CountSegmentsInPlaylist counts EXTINF), F6 the
TruncateToFirstSegments unit pins (which caught a REAL defect: trailing
non-URI lines were dropped even when no truncation engaged; fixed with the
completed-without-truncation flush), F7 a literal {name} test-authoring
artifact, F8 the banned parenthetical-hyphen sweep. F5 documented, not
engineered: an external forward-touch of the prewrite's mtime mid-encode
collapses the window for one poll (a playlist regression the player may
re-prepare on); the fs stat has no defense, the sturdy fix threads the encode
start through the active-encode registries, and that plumbing is folded into
the twins-adoption extraction trigger on the method doc. Exposure judged low
(Linux container, no AV/backup mtime writers on minix).

FINAL STATE: Release -warnaserror build clean (both TFMs); full suite
5265/5265 green on BOTH TFMs (net9.0 + net10.0; main baseline ~5257 + the 8
new pins: ColdEdgeEncode, GrowsWithElapsedAndCapsAtHead, ResumeBeyondWindow,
ResumeInsideWindow, MidWindowResume_OutsideHonorBand,
EpisodePrewriteWindowFloor_RespectsSegmentHoldLookahead, and the two
TruncateToFirstSegments unit pins).
<!-- NOTES:END -->
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
<!-- FINAL_SUMMARY:BEGIN -->
CHOSEN SHAPE: (c) hybrid; the prewrite file stays the full-listing source of
truth, but while the encode is live the serve lists only a time-anchored
growing window (floor 2 entries, +1 entry per 4s, capped at head+1, anchored on
the prewrite's mtime), so the player's live-edge default start (playlist end -
3x TARGETDURATION, confirmed against media3 source and tonight's seg_0353
request) lands on segment 0 instead of the un-encoded tail. Shapes (a) and (d)
were killed by measurement (35s-320s hold vs ~1-2s player tolerance; EXT-X-START
refuted on hardware by the audiobook StartHint round), (b) reintroduces the
mid-content join JF-531 closed.

DEVICE RETEST LIST for Paolo (AC#7):
1. COLD first play of a transcode-tier episode (any non-h264/mpeg4 source,
   e.g. the Sailor Moon R one after its cache is cleared or a new episode):
   must START AT THE BEGINNING, no death; the seekbar grows for the first
   ~40s (encode window) then completes to the full runtime; playback does not
   stall.
2. COLD first play of a remux-tier episode (h264): same expectations, encode
   window shorter.
3. A seek during the first minutes of a cold encode: must work within the grown
   window (seek past the shown bar is not possible by construction).
4. WARM replay of the already-encoded episode: unchanged (instant serve, full
   bar, start at 0).
5. Sentinel (out of scope, JF-778's follow-up trigger): a COLD audiobook or
   >10min single-chapter first play showing the same three-404 death means the
   window must extend to those serves (the song/audiobook prewrite twins).
<!-- FINAL_SUMMARY:END -->

CLOSED (code-side) 2026-10-05 by the orchestrator after the full cycle: merged into main (worker dcfd3b84 + orchestrator tail fff2e06e, --no-ff; the gate-marker's seven axes PASS with the pins re-run in the review tree; its five findings: F4/F5/F2 applied/noted in the tail, F1/F3 the protocol residuals FILED as JF-780 with the discriminating device probes), combined-tree suite 5283/5283 both TFMs. AC#7 (the device round) stays OPEN pending Paolo's cold-Sailor-Moon retest: that retest is the close evidence, and the JF-780 probes ride the same session. Deploying now.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release, -warnaserror, both TFMs)
- [x] #2 dotnet test passes (both TFMs, full suite once on the final state)
- [x] #3 No new compiler warnings introduced (warnaserror build clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (no session attrs touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (none touched)
- [x] #6 NLU test fixtures updated if interaction model changed (not touched)
- [x] #7 E2E test added for new intent or handler logic (n/a: HLS serving; unit pins added instead, both TFMs)
- [x] #8 Locale response strings added to all 17 locales (not touched)
- [x] #9 /simplify passed
- [x] #10 /code-review high passed
<!-- DOD:END -->
