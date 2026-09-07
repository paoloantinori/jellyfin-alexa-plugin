---
id: JF-503
title: >-
  Seek on the episode HLS errors out when it lands beyond the running encode's
  head: hold-for-segment for near-ahead seeks + document the boundary + log
  segment 404s
status: In Progress
assignee: []
created_date: '2026-09-06 08:48'
updated_date: '2026-09-07 04:33'
labels:
  - video
  - hls
  - seek
dependencies: []
references:
  - JF-498
  - device test 2026-09-06
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 device test (item 4 of the verification card): seeking on the episode HLS during playback produced 'various errors' and killed playback, while the same content plays fine start-to-end. Prime mechanism: seek-AHEAD-of-the-encode - the remux runs at ~20x realtime (Silo's 777 segments completed ~3min after start), so a seek beyond the encoded head during the first minutes requests a segment that does not exist yet in the event playlist; ExoPlayer errors hard on the missing segment instead of waiting. A seek WITHIN the encoded head or after completion should work (untested on device; the playlist has ENDLIST only at completion). Options: (a) accept and document the boundary (seek reliable after ~2-3min or within the encoded head); (b) hold-for-segment: when a GetSegment request names the NEXT expected segment of an active encode, block up to ~3-4s until ffmpeg writes it (encode is 20x realtime so the segment is imminent), returning it instead of a 404 - smooths seeks to just-beyond-head; (c) serve a redirect-to-latest for far-ahead seeks (fragile, probably wrong). Recommended: (b) for the near-ahead case + document the far-ahead boundary. Also log GetSegment 404s at debug with the requested name and the highest existing segment, so the next device session can confirm the mechanism from the logs (this session could not: GetSegment does not log).
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

## Implementation Notes (2026-09-07)

Implemented option (b) + observability + boundary documentation. Status stays In
Progress pending the next device session's log confirmation (the hold/miss Debug lines
are the mechanism probe).

### Hold design (option b)

- Endpoint: `VideoAudioController.GetSegment` is now async. On a `FindSegmentPath`
  miss it calls `TryHoldForNearAheadSegmentAsync(itemId, segmentName, RequestAborted)`.
- Eligibility: an encode must be ACTIVE for the item (episode OR audiobook registry),
  the item's HLS directory must exist, and the requested segment number must satisfy
  `highest < requested <= highest + SegmentHoldLookahead` (+2; ~8s of 4s episode
  segments / ~20s of 10s audiobook segments). "highest" = max segment number on disk
  (computed only on a miss; never on the per-segment hot path). A miss BEHIND the head
  never holds (ffmpeg writes sequentially, no backfill); a far-ahead jump never holds.
- Budget: `SegmentHoldBudget` 3.5s total, `SegmentHoldPollInterval` 200ms; the loop
  never waits past the budget (waits `min(poll, remaining)`) and aborts on client
  disconnect via RequestAborted.
- COMPLETION GATE (review-gate finding on the first cut): the hold releases only when
  ffmpeg's LIVE playlist (`stream.m3u8` in the cache dir) LISTS the segment, not when
  the file merely exists. The HLS muxer appends to the playlist only after the segment
  file is closed, while File.Exists can catch the segment mid-write and stream a
  truncated .ts (the same on-device failure class JF-503 fixes, reintroduced
  probabilistically; the hold fires exactly for the segment ffmpeg is writing NOW).
- Scoping across the three encode families: EPISODE remux holds (`_activeEpisodeEncodes`,
  the device-failure path); AUDIOBOOK concat holds (`_activeAudiobookEncodes`: the
  pre-written event playlist lists every segment from first play, so an audiobook seek
  during the first minutes hits the same listed-but-missing 404); single-item SONG path
  deliberately NOT covered (no active flag exists, the encode finishes in seconds, and
  it serves ffmpeg's live playlist which only lists existing segments, so the window is
  sub-second and unreported).
- Boundary (documented at the hold site, `TryHoldForNearAheadSegmentAsync`): seeks
  within the encoded head or ~3s beyond it are smoothed; seeks far beyond the head of a
  running encode still 404 until the encode catches up; after completion (~2-3min for
  a 45min episode at ~20x) all seeks work.

### Observability

- Every GetSegment miss logs Debug: `GetSegment miss: item {id} requested {seg}, highest
  existing segment {n}, activeEncode {b}, holdEligible {b}`.
- Hold outcomes log Debug: appeared-after-Nms / expired-after-Nms(budget) /
  aborted(client disconnected).

### Files

- `Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs`: hold + completion
  gate + miss logging; `TryParseSegmentNumber` shared by the hold and
  `RecordSegmentForTracking`; test seams `SegmentHoldBudget`/`SegmentHoldPollInterval`
  (PlaybackEvictionExemptionTtl pattern) and `SetEncodeActiveForTest`.
- `Jellyfin.Plugin.AlexaSkill/Alexa/VideoAudioCache.cs`: internal
  `FindHlsDirectory(itemId)` (in-memory lookup then scan; the ONE definition of the
  resolution order, now also used by `FindSegmentPath` after the /simplify pass).
- `Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs`: 5
  existing GetSegment tests converted to await the now-async endpoint; 6 new tests
  (hold-appears episode, hold-appears audiobook, hold-expires-404-after-budget,
  far-ahead-immediate-404, no-active-encode-immediate-404, miss-logs-at-Debug probe
  via a capture logger).

### Verification (2026-09-07)

- `dotnet build` (Debug and Release): 0 errors, 0 warnings.
- `dotnet test`: 3395 passed, 0 failed (3381 baseline + 6 JF-503 + 8 from the
  concurrent JF-506 worker on the settled tree; one transient failure during their
  mid-edit compile resolved on rerun).
- Gates: /simplify run (2 cleanups applied: FindSegmentPath reuse of
  FindHlsDirectory; CA1806-compliant parse), review-local run at high rigor (1 finding
  >= 80, the truncated-segment race, FIXED via the playlist completion gate).
- DoD #6/#7/#8 not applicable: no interaction model, handler, or locale strings
  changed (stream-endpoint-only work). #4/#5 untouched and unchanged by the diff.

### Not done (needs the device)

- On-device confirmation that a seek just past the encode head now plays through, and
  that the Debug miss/hold lines show the mechanism. The `/config/logging.default.json`
  override `"Jellyfin.Plugin.AlexaSkill": "Debug"` must be on for the session.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Formal review dispositions (2026-09-07, orchestrator): NO findings at threshold; all six lenses verified against the surrounding production code (the playlist-listing gate's grammar-anchored match, the zero-segment first-request shape, the pin/finally eviction interplay, the converted tests' assertion preservation, the audiobook disk-head boundary matching the documented contract, the FindHlsDirectory hoist behavior-preserving). Two below-threshold observations recorded here per the landing rule: SetEncodeActiveForTest leaks the static registry entry on assertion failure only (per-test random GUIDs, no cross-test interference) - acceptable; the two hold-appears tests carry a 5s/100ms timing bound consistent with the repo's other timing tests.
<!-- SECTION:NOTES:END -->
