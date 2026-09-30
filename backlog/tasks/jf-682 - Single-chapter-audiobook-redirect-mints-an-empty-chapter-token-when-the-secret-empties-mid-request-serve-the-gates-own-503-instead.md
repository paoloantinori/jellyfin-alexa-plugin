---
id: JF-682
title: >-
  JF-682 - single-chapter audiobook redirect mints an empty chapter token when
  the secret empties mid-request; serve the gate's own 503 instead
status: Done
assignee: []
created_date: '2026-09-30'
labels:
  - robustness
  - encode-gate
dependencies:
  - JF-678
references:
  - >-
    backlog/tasks/jf-678 - Token-less-serve-skips-the-JF-499-W3-vanish-probe-PhysicalFile-over-a-vanished-playlist-500s-at-result-execution.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-678 /simplify round (altitude agent finding 2), the one substantive recommendation the round did NOT apply because it changes auth behavior beyond the vanish family's scope and would orphan the no-token pin JF-678 was required to land.

THE SHAPE: `StreamHlsAudiobook`'s route gate (`ValidateStreamToken`) 503s any request when `StreamTokenSecret` is empty ("Stream token secret not configured"), but the SINGLE-CHAPTER redirect further down re-reads the secret and, finding it empty, mints an EMPTY chapter token (`string.Empty`), handing `StreamHlsVideoAudioCore` an override token that flips every serve on the song path into the no-token raw-PhysicalFile branch. The only window is a config save emptying the secret between the gate's read and the redirect's re-mint (same request, microseconds apart), which is also the only reachable entry into the no-token branch JF-678 probed.

WHY THE 503 IS BETTER: a tokenless playlist serve is dead audio either way (its segment lines carry no token, so `GetSegment` 401s every segment), but the current shape answers 200-then-per-segment-401s, which is harder to diagnose than the gate's own 503 with the explicit "secret not configured" log. After JF-678 the shape additionally kicks a full re-encode before handing out the dead playlist, so the wasted work is real.

FIX DIRECTION: at the single-chapter branch, an empty secret should return the gate's own 503 shape (one line, mirroring `ValidateStreamToken`'s decision) instead of minting an empty token. The JF-678 no-token probe stays as defense-in-depth for the branch, but with this fix the branch becomes unreachable in production; the JF-678 no-token pin (`StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode`) drives the CURRENT reachable shape via `SecretClearingLoggerProvider` and would need to be retired or reworked (drive the probe through a different construction) in the same change that lands this.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no intent or handler logic; stream-endpoint auth shape covered by unit pin)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings; the 503 error text already existed verbatim on the gate)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-30 (worker commit on 8b16261a). THE FIX: the empty-secret answer is now ONE shared shape, `StreamTokenSecretNotConfigured()` in `VideoAudioController` (the gate's own log + `StatusCode(503, new { error = "Stream token secret not configured" })`), called from BOTH `ValidateStreamToken` (unchanged behavior, byte-identical answer) and the single-chapter redirect's chapter re-mint: when the secret the re-mint reads has emptied since the route gate (a config save between the two reads), the request ends at the gate's own 503 instead of minting an empty chapter token that would flip every serve in `StreamHlsVideoAudioCore` into the no-token branch (token-less segment lines `GetSegment` 401s, and since JF-678 a full wasted re-encode before the dead playlist). The JF-678 no-token branch and its materialization behavior stay untouched as the last-resort safety net; only the reachability docs moved (the `ServePlaylistWithTokenAsync` "only reachable no-token shape" paragraph now honestly says JF-682 removed the production driver and the branch is a safety net).

TWIN REWRITE: `StreamHlsAudiobook_NoTokenServe_CacheVanishedAtServe_FallsThroughToReencode` was REWRITTEN (not deleted) as `StreamHlsAudiobook_SecretEmptiedAtChapterRemint_ServesGate503_NoReencode`: same construction (SecretClearingLoggerProvider firing on the single-chapter log line, the deleting provider still wired on the fast-path serve log, the 3-digit song-shape fake kept), new verdict, pinning the 503 shape (ObjectResult, status 503, the gate's error string), the provider's Fired flag (the race window was actually exercised), and NO re-encode (no episode-args.txt). RED PROOF RUN (both TFMs): with the check removed and the verbatim old empty-token mint restored, the pin flips exactly as predicted, `Expected: ObjectResult / Actual: ContentResult` (the old materialized no-token serve after the vanish re-encode); fix restored, green again. The `SecretClearingLoggerProvider` doc no longer claims the race reaches the branch; it now names the 503 endpoint.

SUITES: full recipe run on the final state, 4784/4784 both TFMs (net9.0 + net10.0), matching the 8b16261a baseline (test count unchanged: the twin was rewritten in place). GATES: Skill simplify + Skill code-review (high) run on the final diff in the worker transcript.

GATE OUTCOMES. /simplify (4 agents: reuse, simplification, efficiency, altitude): ONE applied finding, the twin's 503-body assert switched from an ad-hoc reflection chain to the file's existing `Body(...)` serialization helper (the sibling-401 assert's established idiom); simplification, efficiency, and altitude all clean (altitude independently re-verified the call map: the redirect is the only non-null overrideToken caller, Mint cannot return empty, every public HLS entry is gate-gated). /code-review high, 5 findings, disposition: (1) the no-token branch lost its only committed test in the twin rewrite, hardening now doc-enforced only: FILED as JF-683 (the dispatch explicitly chose the rewrite-to-503 shape; the branch is unreachable by any public-endpoint test construction post-fix); (2) APPLIED: the twin's "the serve log never fires" claim is now pinned, FileDeletingLoggerProvider gained a Fired flag and GREEN asserts it False (the construction is honest from both directions); (3) single-chapter resume redirect drops ?start= (a one-chapter book resumes from 0:00): PRE-EXISTING, mechanism verified against BuildAudiobookResumeResponse/GetAudiobookResumeUrl and the song core's missing start handling, FILED as JF-684 (outside JF-682's mandate); (4) APPLIED: DoD checkboxes reconciled with status Done (passed items checked, N/A items annotated); (5) six doc blocks retelling the reachability story: REFUTED, the blocks own different aspects (the helper owns the one-answer invariant, the branch doc owns its own reachability and safety-net role, the re-mint comment owns the local guard rationale, the test docs own their construction, the backlog owns history); consolidation would break the local-first readability each site needs, the JF-678 lesson is that each claim must be SHORT and local, which the rewrite made them. DoD 4-8 N/A: no strings, no interaction model, no new intents (the only user-visible string, the 503 error text, already existed verbatim on the gate).
