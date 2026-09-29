---
id: JF-651
title: >-
  JF-651 - the Guid.TryParse+ValidateStreamToken route preamble repeats at ~9
  VideoAudioController entries: extract one shared signed-route validator,
  preserving each route's pinned 400/401 ordering
status: To Do
assignee: []
created_date: '2026-09-27 08:23'
labels:
  - refactor
  - streaming
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 altitude review's item-4 verification (the Guid.TryParse guard investigation walked all the route entries on the way to its verdict).

THE FINDING: the identical request-preamble pair (Guid.TryParse on the route's itemId + ValidateStreamToken for the token check) repeats at ~9 route entries in Controller/VideoAudioController.cs (line anchors at review time: ~221, ~425, ~711, ~1239, ~1332, ~1411, ~1933, ~1982, ~2444). Each copy re-implements the same shape: parse the id, return BadRequest on parse failure, then validate the signed stream token, returning 401 on failure. The JF-637 round deliberately did NOT consolidate these (out of its diff's scope).

THE WORK: extract one shared preamble helper (e.g. ValidateSignedRoute(itemId, token) returning the ValidatedRequest-or-error the routes already consume), migrate all ~9 entries to it. BEHAVIOR CONTRACT to preserve exactly: the 400-vs-401 ordering per route (the altitude review verified the ordering is the documented contract at the speed route: a non-GUID reaches the BadRequest path and yields 400, pinned by StreamHlsAudioSpeed_InvalidItemId_Returns400; missing token on a valid GUID yields 401, pinned by the NoToken sibling test). Read every entry before migrating: any entry whose ordering or error shape differs stays separate with a comment.

VERIFICATION: the existing VideoAudioControllerTests preamble tests stay green unchanged; grep confirms zero remaining inline copies; build 0 errors, suite green both TFMs.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-09-29, commit b9441932 (worktree branch worktree-agent-ab056f03f47ec0da0, not pushed). Anchors at read time: 219/423/709/1237/1325/1407/1925/1979/2441; every entry read before migrating. Extracted signature: `private ActionResult? ValidateSignedRoute(string itemId)` in VideoAudioController.cs, placed above `ValidateStreamToken` (400 `Invalid itemId format` on a non-GUID id BEFORE the JF-309 token check; the token check's 401/503 passthrough; null = proceed).

MIGRATED (6): StreamVideoAudio, StreamHlsVideoAudio, StreamHlsEpisode, StreamHlsEpisodeAudio, GetEpisodeAudioSegment, GetSegment. The two segment routes were the task's canonical inline pair (identical text). The four playlist routes were the guarded shape (token check only when the id parsed; 400 deferred to `ValidateVideoAudioRequest` as the FIRST statement of the route/Core): hoisting the 400 into the preamble is observably identical (same status, same body, earlier in code), verified per input class by code-review high; `ValidateVideoAudioRequest`'s own parse branch stays (still reached by the kept-separate speed route and the internal Core callers).

KEPT SEPARATE (3), each with an in-code comment naming the difference: StreamHlsAudioSpeed (the Core's unserved-rate 400 fires BEFORE the id 400, so a non-GUID + unserved-rate request must keep answering "Unsupported playback rate"; hoisting would flip the body). GetAudioSpeedSegment (the unserved-rate 400 sits BETWEEN the id 400 and the token 401; a combined preamble would reorder them). StreamHlsAudiobook (the id is a parentId: distinct "Invalid parentId format" body, and the parse's GUID feeds the children query).

Token gate NOT weakened anywhere: every route still validates before serving. code-review high caught the helper doc's per-route-pair pin claim running ahead of the suite, so two pins were ADDED (existing tests untouched): StreamHlsVideoAudio_NoToken_Returns401 and GetEpisodeAudioSegment_InvalidItemId_Returns400; each migrated route now genuinely owns the InvalidItemId/NoToken pair.

VERIFICATION TAIL: build 0 errors 0 warnings both TFMs; VideoAudioController filter 213/213 both TFMs (pre-final tree); full suite on the final tree 4718/4718 net9.0 + 4718/4718 net10.0 (4716 baseline + the 2 new pins), preamble tests unchanged; grep shows zero remaining inline copies of the identical pair (only the 3 documented divergent sites and the helper itself).

GATES: /simplify (4 parallel angles: doc-citation fix applied; IsNullOrWhiteSpace-before-TryParse skip kept as house style mirroring ValidateVideoAudioRequest; the surfaced speed-sibling 400-precedence inconsistency filed same-turn as JF-664). code-review high (verdict behavior-preserving; both findings applied: doc claim made true via the 2 pins).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as worktree commit b9441932 (not pushed): the Guid.TryParse+ValidateStreamToken route preamble consolidated into one private ValidateSignedRoute(itemId) helper at 6 of the 9 VideoAudioController entries (StreamVideoAudio, StreamHlsVideoAudio, StreamHlsEpisode, StreamHlsEpisodeAudio, GetEpisodeAudioSegment, GetSegment), with the pinned 400-before-401 ordering preserved per route; the three divergent entries (both audio-speed routes, whose rate-400 interleaving differs from each other, and the audiobook parentId route) stay separate with comments naming their differences, and the pre-existing speed-sibling 400-precedence inconsistency is tracked as JF-664. Two pin tests added so the helper's contract citation is true; suites 4718/4718 both TFMs with existing preamble tests unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
