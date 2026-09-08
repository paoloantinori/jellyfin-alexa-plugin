---
id: JF-500
title: >-
  HEVC video sources still cannot play on the Echo Show (static kept with
  warning): add the video transcode tier to the episode HLS path
status: Done
assignee: []
created_date: '2026-09-05 20:32'
updated_date: '2026-09-08 20:00'
labels:
  - tv
  - video
  - transcoding
  - echoshow
dependencies: []
references:
  - JF-498
  - corr=d9f848a7
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from JF-498's live verification (2026-09-05): the routing policy works and the remux path serves h264+eac3 sources, but HEVC-video sources (the entire Adolescence series - the one Paolo tested with - plus scattered HotD/Silo/The Bear/Rick and Morty/Bluey episodes) keep the static URL with a named-codec warning and cannot play on the Echo Show (H.264 only). The first cut deliberately excluded video transcoding. This task adds it: HEVC/AV1 video -> H.264 transcode in the episode HLS pipeline (video re-encode instead of copy; audio as today). Considerations: CPU cost on the minix hardware (HEVC decode + H.264 encode must sustain >= 1x realtime or playback catches up to the encode; measure first-segment time and sustained rate on the real box before promising anything; 4K HEVC sources may need scaling to 1080p to keep realtime - read the source resolution and decide, possibly behind a config flag for the transcode tier); quality preset (veryfast/ultrafast trade-off); the encode cache budget at transcode bitrates (the C1 playback pin already protects being-watched entries). Acceptance: an Adolescence episode plays end-to-end on the Echo Show with the video actually visible.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MEASUREMENTS (2026-09-08, minix, Adolescence E1 = 1920x1080 HEVC 4077kbps + aac95/eac3 768, 65min; the exact pipeline the tier will run: -c:v libx264 -preset X -crf 23 -g 48 -c:a aac -ac 2 -b:a 192k, HLS 10s segments, 90s of content per run): ultrafast wall 20.4s = 4.40x realtime (9 segments, ffmpeg speed 4.48x); veryfast wall 42.3s = 2.12x (speed 2.14x); load peaked ~4 on the 4-core box. DECISION: ultrafast CRF23 for the tier (4.4x headroom absorbs concurrent playback/encode load - the JF-515 incident showed the box at load 5+ during heavier activity, where a 2.1x encode could dip near stall); veryfast rejected on margin. LIBRARY SCAN: 91 HEVC episodes, ALL <=1080p (1920x960..1080; no 4K today) - but the tier still adds a height>1080 scale guard as insurance. Cache-budget note for the implementation: the current EstimateEncodeBytes (64MB/h) is the 1fps-black-frame AUDIO path constant; the video tier produces ~2-3GB/h at CRF23 ultrafast and needs its own constant in the same formula shape. Audio policy unchanged (aac transcode as today). First-attempt pitfall repeated from JF-517 and avoided: scratch dirs must be created INSIDE the container (host mkdir + container ffmpeg = instant silent failure).

REVIEW ROUND 2 (2026-09-08, code-review high on the working-tree diff AFTER the F1/F2 fix round; F1/F2 implementation itself verified line-by-line correct, build 0 warnings, suite 3516/3516). Findings on the FEATURE diff, unfixed this round (out of the F1/F2 dispatch scope; decide before closing JF-500):

R1 (high): endpoint tier pick can disagree with the handler probe and cache the wrong result forever. ResolveSourceVideoCodec takes the FIRST video stream and returns null on blank codec, while VideoAppStreamPolicy.ExtractCodecs skips blank-codec streams; TryGetMediaStreams also swallows transient GetMediaStreams failures to null. Either divergence -> videoTranscodeTier=false -> -c:v copy remux of undecodable HEVC bytes -> completes with ENDLIST -> ValidateEpisodeCacheAsync accepts it and the cache-hit path never re-probes. Also the in-code justification at VideoAudioController.cs ~:579-584 ('handler-side probe leaves such items on the static route') is stale post-JF-500.

R2 (high): Decide routes ANY known non-h264 first video stream to the transcode tier and the args builders -map 0:v:0, so an item whose first video stream is an attached mjpeg cover gets the COVER re-encoded as the video track (frozen frame + audio) instead of the working static URL it had pre-JF-500. Same for multi-video-stream items whose real track is not first.

R3 (medium): the transcode scale guard fails open on unknown height (null Height -> no -vf) while HlsMonitorTimeoutMinutes assumes >=2.0x realtime; a >1080p source with unprobed Height transcodes full-size at ~1.1x and gets killed mid-encode (the F1 churn). Suggested: unconditional ffmpeg clamp scale=-2:'min(ih,1080)' removes the probe dependency and the fail-open.

R4 (decision, from the F1 fix): HlsMonitorTimeoutMinutes keeps the flat 30-min kill when RunTimeTicks is null/0 (pre-decided 'null => 30 floor for safety'), so >132min HEVC movies with missing runtime metadata still hit the original F1 hard-kill. A higher unknown-runtime ceiling for the transcode tier alone would close it; trade-off is only how long a hung encode holds the F2 slot.

R5 (decision, from the F2 fix): the serialize-slot wait is unbounded and uncancellable (no call site passes a CancellationToken; HttpContext.RequestAborted is not wired anywhere on these endpoints). A queued second transcode blocks for the whole first encode (bounded in practice by the F1 monitor kill) and then spawns a full multi-GB encode even for a client that already left; mitigant: the encode still completes and populates the cache for the next request. Options if pursued: bounded WaitAsync returning a busy error, or RequestAborted wiring.

R6 (cleanup, F2 fix): serializeSlot is a nullable param with a hand-maintained conditional acquire + four release sites; an IDisposable lease bundle (slot+gate+pin) released in one finally would make the inverse-order pairing structural instead of comment-enforced. Verified correct on all four current paths.

R7 (cleanup): the FlatHourlyEncodeBytes extraction missed its third copy: EstimateEpisodeEncodeBytes' unknown-bitrate fallback still inlines the round-up-hours+floor computation its two siblings now delegate.

R8 (design note): the new HlsTranscode enum value is behaviorally identical to HlsRemux at its only runtime consumer (BaseHandler collapses every non-Static route to the same episode-HLS URL) while the endpoint re-derives the tier independently; make GetVideoAppLaunchUrl an explicit switch so a future route value fails loudly instead of falling into the catch-all.

Test-side cleanups (inline-copy migrations, concurrency-test skeleton sharing, triple media-stream probe fold) are tracked in JF-525.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped, measured, live-verified autonomously up to the endpoint. MEASUREMENT (2026-09-08, real box, Adolescence E1 1080p HEVC): libx264 ultrafast CRF23 = 4.40x realtime (veryfast 2.12x rejected on margin); live sustained rate on the deployed build = 3.5x with the box also serving. ROUTING: VideoAppStreamPolicy.Decide sends known non-h264 video to the new HlsTranscode route; BaseHandler.GetVideoAppLaunchUrl is the single chokepoint (all 11 Movie/Episode launch sites); the endpoint picks the tier by codec - remux ONLY on known h264, unknown/null fails toward the TRANSCODE (asymmetric cost: an extra encode vs a copy-remux of undecodable bytes cached forever, review R1). Attached-picture covers (mjpeg/png) skipped on both the codec pick and the args map (0:V:0; ffmpeg semantics probed). ARGS: libx264 ultrafast CRF23 g48 + pix_fmt yuv420p (10-bit High-10 guard, probed) + UNCONDITIONAL scale=-2:min(ih\,1080) clamp. SAFETY: monitor kill scaled per tier (runtime/2.0x + 10min, 30 floor, 120 unknown-runtime; review F1 - the old flat 30min killed >2h12m movies mid-encode); one transcode at a time via a dedicated slot before the shared gate (review F2 - two concurrent encodes blocked music paths). CACHE: 3072MB/h flat estimate via the shared FlatHourlyEncodeBytes. HOTFIX on the first deploy (live-caught): the clamp token had shell-style quotes (ArgumentList passes them literally: 'Error initializing filters') and a dropped 'ih,' inside min() - corrected to the escaped-comma form, verified with a real encode before and the failing request after. Gates: /simplify (2 passes), code-review high (FIX FIRST F1/F2/R1-R4 all applied, several empirically probed; R5 skipped-documented; R6-R8 -> JF-525), tests 3520/3520 branch+main. LIVE VERIFICATION (autonomous maximum): playlist 200 + valid HLS headers, live process carries the full measured arg set, 3.5x sustained, tier decision logged, stderr aggregation 0 lines. REMAINING (device, needs Paolo): the acceptance 'Adolescence plays end-to-end on the Echo Show with video visible' - the episode will be fully cached when tested. DoD 4-8 N/A.
<!-- SECTION:FINAL_SUMMARY:END -->
