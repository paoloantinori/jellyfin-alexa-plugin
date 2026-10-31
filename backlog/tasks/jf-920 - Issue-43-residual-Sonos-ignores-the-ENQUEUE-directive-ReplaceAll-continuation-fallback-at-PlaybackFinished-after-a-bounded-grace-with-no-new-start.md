---
id: JF-920
title: >-
  Issue #43 residual: Sonos ignores the ENQUEUE directive; ReplaceAll
  continuation fallback at PlaybackFinished after a bounded grace with no new
  start
status: In Progress
assignee: []
created_date: '2026-10-31 14:05'
updated_date: '2026-10-31 14:53'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
  - claudedocs/research_simulate-skill-locale-outage_2026-10-31.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to JF-918 / issue #43 (reporter's on-device test 2026-10-31, comment issuecomment on #43): the split-device canonicalization WORKS on the Sonos Era 100 (log-verified: "Split-device identity canonicalized" on Started/NearlyFinished/Finished; queue resolves "5 items, resolved next item"; the plugin emits AudioPlayer.Play ENQUEUE with correct next token + expectedPreviousToken in ~60ms). BUT the Sonos never starts the enqueued stream: no PlaybackStarted for track 2, no PlaybackFailed; PlaybackFinished for track 1 arrives at natural end and physical playback stops. Also: the Sonos fires PlaybackNearlyFinished ~0.4s AFTER PLAYBACK STARTS (not near track end) - both in the original report (+0.5s) and now (+0.4s).

INTERPRETATION: the platform's AVS implementation on the Sonos ignores (or drops) the ENQUEUE playBehavior - either partial AudioPlayer support on Alexa-built-in third-party devices (H1) or an enqueue issued seconds/minutes before the boundary being dropped by its queue management (H2, consistent with the anomalously early NearlyFinished). Both hypotheses manifest identically at the plugin boundary: PlaybackFinished arrives, our JF-691 enqueue record exists for that boundary, and the enqueued stream NEVER started. The reporter's index observation (MoveTo 0->1 at enqueue, Finished logs index=0 of 5) is expected, not a bug: the Finished arm's index names the FINISHING item resolved from its token - verify while implementing and answer in the reply.

FIX DESIGN (works for both hypotheses, no config, no regression on Echo): in PlaybackFinishedEventHandler's existing keep-alive arm (the "playback continues (index=N of M)" branch, which already knows the JF-691 EnqueuedForThisBoundary verdict), detect the IGNORED-ENQUEUE shape: the boundary has our enqueue record AND the enqueued item never started (no PlaybackStarted for it per the ordering/active evidence: PlaybackReportOrdering's LastStart still names the finishing token, and/or the JF-655 active flag never moved). When detected, respond with AudioPlayer.Play REPLACE_ALL of the next queue item instead of the keep-alive (event responses may carry AudioPlayer.Play per the JF-299 contract). Cost on such devices: a sub-second gap per track (gapless lost; acceptable vs stopping). Echo safety: on real Echos the enqueued stream starts at the boundary, so Started(new) is in the ordering state by the time Finished(old) is processed (or within ms); the verdict is only taken when the enqueued item NEVER started, so the byte-identical keep-alive stays for every started-enqueue shape. Named residual, accepted: an Echo race where Started(new) is delayed multi-second after Finished(old) would restart the next track from 0 once (not a documented ordering shape; the documented displacement ordering is Started(new) BEFORE Stopped/Finished(old)).
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
IMPLEMENTED (2026-10-31): PlaybackReportOrdering gains the internal accessor GetLastStartedItemId(deviceId) (null = no start recorded, deliberately NOT ignored-enqueue evidence). PlaybackFinishedEventHandler's JF-691 keep-alive arm gains the fallback: when the boundary has our enqueue record AND the ordering state positively shows a last-started item that is NOT the enqueued one, respond with AudioPlayer.Play ReplaceAll of the record's next item (rate continuation from the finishing item's launch scope; the builder records LastPlayed/LaunchBase), clear the enqueue record (duplicate-Finished double-launch guard), and log at Information. Null evidence keeps the byte-identical keep-alive (all pre-existing tests unchanged). Tests: 2 pins on the SeedTwoTrackJf691Scene harness (Sonos shape: real NearlyFinished enqueue + BeginStart(track1) -> ReplaceAll with track2 token, session-ending, record cleared, Info line; Echo shape: BeginStart(track2) -> keep-alive byte-identical, record intact). Build 0 warnings both TFMs; suite 6139/6139 per TFM. The reporter's index question verified while implementing: the keep-alive line's index is finishedIndex, the FINISHING item resolved from its token (answered in the posted reply, issuecomment-6544434490). Gates in flight: /simplify battery; code-review high; then deploy + fresh dev build + reply.

2026-10-31 REVIEW ROUNDS (supersedes the first plan note's instant-verdict description): /simplify battery applied (Guid.Empty neutrality in GetLastStartedItemId; the accessor doc now describes the neutral shapes). code-review high round 1 found the CRITICAL instant-verdict flaw: the repo's own JF-683 live evidence is that a healthy Echo's Finished arrives DURING the ~1s inter-track gap BEFORE the enqueued stream starts, so last-started==finishing at that instant on every healthy device; the instant design fired ReplaceAll at every Echo boundary (destroying gapless everywhere). REDESIGN: no instant verdict exists; the handler now WAITS a bounded grace (EnqueueHonorGraceMs=2000, poll 250ms; fast-path exit when the last start already moved; cancellation ends the wait HONORED): an Echo's Started(new) lands mid-grace (byte-identical keep-alive), a dropping Sonos never starts anything (ReplaceAll of the enqueued item). Repeat-one and any same-item enqueue are EXCLUDED on purpose (the item-id signal cannot see a same-item restart, and a bare-token same-item ReplaceAll would reopen the JF-655 nonce window): repeat-one on dropping devices keeps pre-JF-920 behavior, with the StartMarker generation named as the future seam. Other applied findings: clear-before-build (concurrent duplicate Finished double-launch), libraryManager threading into the builder (the VideoApp delegation's concat climb), the test-comment hyphen, the mock-preamble hoist. Tests now 4 pins (Sonos drop paying the full 2s grace; the decisive mid-grace Started regression pin; the already-started fast path; the unrelated-item hijack). Build 0 warnings both TFMs; suite 6141/6141 per TFM. Honest cost statement: every natural Echo track boundary now answers its Finished ack up to ~1s later (an event ack carries no speech and the enqueued stream is already starting device-side; well under the 6s budget).
<!-- SECTION:PLAN:END -->

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
