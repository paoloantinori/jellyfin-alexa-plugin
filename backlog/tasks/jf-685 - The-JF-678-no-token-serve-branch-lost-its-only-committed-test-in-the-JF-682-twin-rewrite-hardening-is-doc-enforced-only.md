---
id: JF-685
title: >-
  JF-685 - the JF-678 no-token serve branch lost its only committed test in the
  JF-682 twin rewrite; its materialization hardening is doc-enforced only
status: Done
assignee: []
created_date: '2026-09-30'
updated_date: '2026-10-01'
labels:
  - encode-gate
  - test-gap
dependencies:
  - JF-682
references:
  - >-
    backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
  - >-
    backlog/tasks/jf-678 - JF-678-token-less-serve-skips-the-JF-499-W3-vanish-probe-PhysicalFile-over-a-vanished-playlist-500s-at-result-execution.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-682 code-review round (high effort, finding 1 of 5).

THE GAP: `StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode` was the ONLY committed test driving `ServePlaylistWithTokenAsync`'s no-token branch (materialized read via `ResolveServeContentAsync`, vanish-to-re-encode fall-through), and JF-682 rewrote it in place into the 503 pin (`StreamHlsAudiobook_SecretEmptiedAtChapterRemint_ServesGate503_NoReencode`) because the 503 closed the branch's last production driver. Since JF-682 the branch is reachable by NO test construction through the suite's public-endpoint idioms: every public HLS entry is gate-gated (401 no token / 503 empty secret) and the redirect's overrideToken can no longer be empty. A future edit reverting the branch to a raw `PhysicalFile` (or dropping `ResolveServeContentAsync` there) re-introduces the exact JF-678(a) bug (a 500 at result execution over an evicted playlist) with all 4784 tests staying green. The one-time manual red proof documented in the twin's doc is not a committed guard.

CANDIDATE CONSTRUCTIONS (the trade-off this task must decide): (a) an internal seam (make the serve family or a driver internal + InternalsVisibleTo-style test access) so a test can drive the branch through a real construction; (b) reflection-invocation of the private method (works today, novel fragile pattern for this suite); (c) accept the branch as a documented safety net with no committed pin (a decision, recorded, not a fix). The JF-682 dispatch explicitly chose the rewrite-to-503 shape, so this task owns the residual, it is not a regression JF-682 introduced beyond the trade-off already taken.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-gap pin only; the stream-endpoint auth/vanish shapes are covered by unit pins, no new intent or handler logic)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-01 (worker on fc842406). THE DECISION (task candidate (a), the "driver" variant): the token-free core `StreamHlsVideoAudioCore` went private -> internal (InternalsVisibleTo, ALREADY wired in the csproj for the test assembly; visibility only, zero behavior change, no new route: internal methods are not ASP.NET action candidates), because driving `ServePlaylistWithTokenAsync` itself can exercise only the branch's probe and misses exactly the vanish translation + re-encode fall-through the JF-678(a) pin exists for, and reflection has no suite precedent. The gate-to-remint history and the INVARIANT the old private visibility enforced for free (this core performs NO token validation; every production caller must arrive through a validated gate, the JF-309 rule) are stated on the seam's doc.

THE PIN: `StreamHlsVideoAudioCore_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode` (VideoAudioControllerTests, in the JF-678 section) re-drives the orphaned JF-678(a) shape end-to-end through a REAL construction: the core invoked with a token-less request query (`CreateController(null, ...)`), so every serve inside it takes the no-token branch; planted ENDLIST cache; the deleting provider on the fast-path serve log; the 3-digit song-shape fake. Asserts: the deleting provider FIRED (the construction honesty pin, green-by-design in the red shape), the vanish translation's "vanished or became unreadable" log wording (the ProbePlaylistExists conflation-decision honesty contract, whose only pinner the orphaned original carried), the MATERIALIZED ContentResult with the fresh playlist's RAW bytes (no `?token=`, the inverse of the tokened twins' assert), the re-encode fall-through (episode-args.txt), and exactly TWO full reads through ReadPlaylistContentAsync (the verdict's + the post-encode fresh read; the vanish serve consumed the threaded content probe-only, the JF-677 one-read invariant extended to the no-token branch). RED PROOF RUN TWICE on both TFMs (before and after the review round's added asserts): reverting the branch to the raw `PhysicalFile` flips the pin, first at the vanish-log assert, then (original round) `Expected ContentResult / Actual PhysicalFileResult` over the DELETED path; no re-encode, reads drop to the verdict's single read. Exactly the filed JF-678(a) bug shape.

SUITES: the exact recipe (`env -u NUGET_PACKAGES -u NUGET_HTTP_CACHE_PATH dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`, never --no-build) on the final state: 4831/4831 net9.0 + 4831/4831 net10.0 (baseline at fc842406 was 4830 per TFM; +1 = the new pin), exit 0, no new warnings.

GATES. /simplify (4 angles, parallel agents): reuse clean, efficiency clean, altitude clean; simplification 3 findings ALL APPLIED: the no-public-construction rationale was duplicated across the controller doc and the test doc (controller doc compressed to the seam fact + pointer; the full why stays on the test doc), the controller doc's redundant final sentence deleted with it, and "the funnel" metaphor replaced by the concrete "exactly TWO full reads through ReadPlaylistContentAsync". /code-review high, 3 findings, disposition: (1) APPLIED - the orphaned original's "vanished or became unreadable" translation-log pin restored into the new test (it had ZERO coverage in the tree since the rewrite; red-flip re-measured after adding it); (2) APPLIED as doc - the internal surface's lost compile-enforced gate-bypass invariant named on the seam doc (the full compile-level fix is impossible by construction: ANY seam widens the surface; reflection would trade a doc-invariant for a fragile novel pattern; the reviewer's own probe confirmed internal creates no route and IVT reaches only the test assembly); (3) CUT AT THE CAP, FILED same-turn as JF-692 (extract a shared song-path vanish fixture helper, the SetupAudiobookVanishFixture sibling, from the four near-identical song pins) - applying it would rewrite three green sibling pins inside a test-gap task, and the four constructions differ in provider trigger, planted bytes, and lock choreography, so the helper's parameterization is its own design decision. DoD 4-8 N/A: no session-attribute code, no HttpClient code, no interaction model change, no new intent/handler logic, no new strings.
<!-- SECTION:FINAL_SUMMARY:END -->
