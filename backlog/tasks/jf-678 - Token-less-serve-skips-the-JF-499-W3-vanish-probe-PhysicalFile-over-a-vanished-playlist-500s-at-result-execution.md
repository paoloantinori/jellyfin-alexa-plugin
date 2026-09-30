---
id: JF-678
title: >-
  JF-678 - token-less serve skips the JF-499 W3 vanish probe: PhysicalFile
  over a vanished playlist 500s at result execution
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - encode-gate
  - robustness
dependencies: []
references:
  - >-
    backlog/tasks/jf-677 -
    JF-677-double-playlist-read-at-every-validated-warm-cache-serve-verdict-reads-the-full-file-serve-rereads-it.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-677 code-review round (high effort, finding 2 of 5), the pre-existing gap the review surfaced while auditing the new vanish probe.

THE GAP: `ServePlaylistWithTokenAsync` (and the serve family generally) returns `PhysicalFile(playlistPath, ...)` on the NO-TOKEN branch BEFORE `ResolveServeContentAsync` ever runs, so a token-less serve holding a verdict-validated (or fresh) path performs NO vanish probe and NO read. When the playlist directory is deleted between the cache/verdict read and result execution (a concurrent debris verdict or eviction sweep, the JF-499 W3 race), the exception fires at RESULT-EXECUTION time, outside `TryServeValidatedHlsCacheAsync`'s catch: a 500 that only self-heals on the Echo's playlist retry, exactly the shape JF-499 W3 was filed to remove for the content branches.

SCOPE AND REACHABILITY: the skill always mints `?token=` on its URLs (JF-309), so the exposure is non-skill/manual fetches; the review also named the single-chapter audiobook redirect with an empty `StreamTokenSecret` as a reachable shape. Behavior is UNCHANGED by JF-677 (the old sync serve had the same early PhysicalFile return), which is why JF-677 documented the exception instead of widening its diff.

FIX DIRECTION: run the same existence probe on the no-token branch when preloaded content or a preceding verdict exists (or unconditionally), throwing the same `FileNotFoundException` into the existing vanish-translation paths; needs its own red proof (a token-less vanish-at-serve pin, mirroring the FastPathCacheVanishedAtServe pair) because it is a behavior change (500-at-execution becomes re-encode fall-through), and a decision on whether the token-less branch deserves the fall-through at all (a manual fetcher might prefer the 404/500 over silently kicking a re-encode).
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

ORCHESTRATOR GATE-MARKER EXTENSION (2026-09-30, same-turn from the JF-677 marker round): this task now owns the WHOLE vanish-at-serve hardening family surfaced by that round, not just the no-token branch probe: (1) the three in-lock verdict+serve rows (song ~534, episode ~889, variant ~1916) let the probe's FileNotFoundException propagate unhandled to a bare 500 on the verdict-to-serve race (pre-existing; the old fresh read propagated identically); (2) the two audiobook content-branch sites' generic catch falls through to PhysicalFile over the now-vanished path, a 500 at result execution outside every catch; (3) the song path has NO vanish-at-serve pin at all (the FastPathCacheVanishedAtServe pair covers episode + variant only) - the song twin belongs with whatever hardening lands. Also note for the fixer: the probe's File.Exists conflates inaccessible with vanished (an ACL revocation between verdict and probe becomes a silent vanish translation -> CleanupHlsStub + re-encode attempt on an unreadable dir, where the old read's UnauthorizedAccessException failed loudly) - decide deliberately whether the loud failure is wanted back.

## Design (JF-678, written before the code)

THE UNIFYING SHAPE: ONE vanish idiom for every serve row, in two halves. (1) THROW HALF: every serve of a playlist that followed a verdict (or an existence gate) resolves its bytes through `ResolveServeContentAsync`, whose threaded path probes existence, and the raw no-token serves (which read nothing) get the SAME probe factored into one shared `ProbePlaylistExists` helper; a vanished-or-unreadable file throws the canonical `FileNotFoundException` from the serve, never from result execution. (2) TRANSLATE HALF: the verdict+serve composite `TryServeValidatedHlsCacheAsync`, whose catch IS the translation (the design's first sketch used a dedicated wrapper beside it; the simplify round folded that back into the composite, so every verdict row shares ONE symbol), converts that throw into a null so the caller falls through to its existing next step: the fast paths to the lock+re-encode (today's behavior, unchanged), the three in-lock verdict+serve rows (song/episode/variant) to the encode branch BELOW them inside the same lock (structurally safe: the fall-through lands exactly where a null verdict already lands; the vanished directory is recreated by the encode branch's `Directory.CreateDirectory`), and the audiobook's two verdict rows (fast path + in-lock double check) to the guard/lock path. The two audiobook content branches (`ServeAudiobookPlaylistAsync`/`ServeResumePlaylistAsync`) stop swallowing the vanish: their generic catches get an exception filter excluding the vanish set, so the vanish reaches the translation while every NON-vanish failure keeps today's `PhysicalFile` fallback verbatim.

PER-SITE DISPOSITIONS: (a) no-token branch of `ServePlaylistWithTokenAsync` (+ the audiobook no-token tail): probe added; DECISION on the fall-through question: the token-less serve DOES get the re-encode fall-through, because the only reachable token-less shape today is the single-chapter audiobook redirect racing a secret being emptied between the route gate and the chapter re-mint (every public HLS entry is token-gated, and the gate 503s on an empty secret, so a manual token-less fetch 401s before any serve); in that shape the caller is the normal skill flow, a fresh re-encoded playlist beats a terminal 500, and the "manual fetcher" the filing worried about cannot reach the branch at all. (b) three in-lock rows: hardened as above, each with its vanish twin. (c) audiobook: catch filters + the two verdict rows wrapped. DELIBERATELY NOT HARDENED (documented residuals, each strictly status-neutral or better than today): the audiobook CONCURRENT-GUARD rows and FIRST-FETCH rows, and the song/episode PREWRITE-helper serves reached from unwrapped sites (the fast-path song gate at the pre-verdict position and the first-serve sites): each serves a file that a live encode just created inside its JF-428-pinned directory, so a vanish there requires the pin protocol itself to fail; after the catch-filter change such a vanish surfaces as an action-time 500 (logged with a stack) instead of a result-execution 500 over a dead path, which keeps a pin breach LOUD instead of masking it behind a retryable 503, and a true re-encode fall-through is structurally impossible there (it would mean restarting the endpoint inside the lock it already holds). CONFLATION DECISION (the extension's closing question): the probe's `File.Exists` conflation of inaccessible-with-vanished is KEPT deliberately: the window is only the verdict-to-serve gap, the fresh-read era's "loud" `UnauthorizedAccessException` was itself a 500, and the re-encode attempt the conflation kicks off re-discovers the access failure loudly at its own writes; the price is honest wording, so the translation's log now says "vanished or became unreadable" instead of claiming a pure vanish, and the probe helper's doc carries the decision.

## Implementation + red-proof record (JF-678)

Production diff: `ProbePlaylistExists` (the named probe, used by `ResolveServeContentAsync`'s threaded path AND `ServePlaylistWithTokenAsync`'s no-token branch), `IsHlsVanishException` (the family's ONE exception-set predicate, used by the validator core's catch, the composite's catch, and the two audiobook fallback filters), the verdict+serve composite `TryServeValidatedHlsCacheAsync` now also carrying the three in-lock verdict+serve rows (song/episode/variant, each falling through to its own encode branch) and the audiobook fast row + in-lock row, and the two audiobook content-branch catches filtered (`!IsHlsVanishException(ex)`). NOT hardened, documented at the guard site: the audiobook concurrent-guard rows + first-fetch rows and the prewrite helpers' unwrapped call sites (live pinned encode's files; a vanish there is a JF-428 pin breach that now surfaces as a loud action-time 500 instead of a result-execution 500; no fall-through exists structurally). SCOPE TRIM during implementation: `ServeAudiobookPlaylistAsync`'s raw no-token tail got NO probe, because that route's query token is gate-validated (no override-token mechanism exists there), so the tail's no-token entry is unreachable, and its reachable entry is the non-vanish catch fallback whose PhysicalFile degrade must stay verbatim (a probe there would have converted every non-vanish read failure into the vanish path and effectively killed the documented fallback).

Seven twins added (all in VideoAudioControllerTests, both TFMs green): StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode (the (a) pin; new SecretClearingLoggerProvider helper reproduces the ONLY reachable no-token shape: the single-chapter redirect whose secret is emptied between the route gate and the chapter re-mint), StreamHlsVideoAudio/StreamHlsEpisode/StreamHlsEpisodeAudio_InLockCacheVanishedAtServe_FallsThroughToReencode (the (b) trio, on the JF-677 ServeInLockWarmCacheAsync core + the deleting provider on each row's own serve log), StreamHlsAudiobook_FastPathCacheVanishedAtServe_FallsThroughToReencode + _InLockCacheVanishedAtServe_FallsThroughToReencode + _FastPathCacheVanishedAtServe_WithResume_FallsThroughToReencode (the (c) trio; the WithResume twin pins ServeResumePlaylistAsync's filter, the others pin the token-branch filter).

RED PROOFS (each hardening independently reverted, its twin run on net9.0, then restored):
1. no-token probe removed -> the (a) twin fails on "the re-encode path must have run" (the serve answers PhysicalFile over the DELETED path; no vanish log).
2. song in-lock wrapper removed -> FNF "Playlist vanished between validation and serve" rethrown out of StreamHlsVideoAudioCore (the bare-500 shape).
3. episode in-lock wrapper removed -> same FNF out of StreamHlsEpisodeCore.
4. variant in-lock wrapper removed -> same FNF out of ServeVariantHlsAsync.
5. audiobook fast wrapper removed (filter still on) -> same FNF out of StreamHlsAudiobook.
6. audiobook in-lock wrapper removed -> same FNF.
7. resume catch filter removed -> the WithResume twin fails IsType(ContentResult) vs PhysicalFileResult: the catch swallows the vanish and answers PhysicalFile over the dead path (the filed shape verbatim).
8. token-branch catch filter removed -> the audiobook in-lock twin fails the same PhysicalFile-over-dead-path shape.

## /simplify round (JF-678, applied after the first green suite)

Four parallel cleanup agents (reuse / simplification / efficiency / altitude), then a high-effort code-review pass over the final diff. APPLIED: (1) the five wrapped sites now call the EXISTING verdict+serve composite `TryServeValidatedHlsCacheAsync` instead of a new parallel wrapper (behavior-identical: each per-site serve log moved inside the serve closure at the same firing point, preserving the JF-499 W3 log-ordering contract; the interim wrapper helper was deleted, the composite got its original try-around-validate-and-serve body back plus the widened "vanished or became unreadable" wording); (2) `IsHlsVanishException`, the family's ONE exception-set predicate, shared by the validator core's catch, the composite's catch, and the two audiobook fallback filters; (3) `WriteRecordingFakeFfmpeg` gained the first-segment-name parameter (song 3-digit vs the 4-digit default), replacing three byte-identical inline fake bodies including the pre-existing JF-677 copy; (4) `SetupAudiobookVanishFixture`, the shared two-chapter book fixture of the three audiobook twins; (5) the guard-site residual comment trimmed to a pointer at the coverage boundary; (6) the coverage-boundary doc now also names the MP4 sibling endpoint (`StreamVideoAudio`) as deliberately outside the contract (single cached file, no segment directory, self-heals as a cache miss). All five wrapper reds were RE-RUN against the final composite shape and still flip their twins: the FileNotFoundException rethrows out of the song/episode/variant/audiobook-fast/audiobook-in-lock rows. SKIPPED, with reasons: extracting the fire-once logger provider scaffolding (second occurrence, below the repo's extract-at-third convention) and the SetupLibraryItems mock collapse for the audiobook twins (the file's dominant convention is inline setup; the helper covers only GetItemById). FILED NOT APPLIED: the altitude agent's proposal to make the single-chapter redirect answer the gate's own 503 on an empty secret instead of minting an empty chapter token. It is a real improvement (a tokenless serve is dead audio either way, and 503-with-log beats 200-then-per-segment-401), but it changes auth behavior beyond the vanish family's scope and would orphan the no-token pin this task was required to land; it is backlog/tasks/jf-682.
