---
id: JF-563
title: >-
  Resume and StartOver of audiobooks bypass NativeControlsForBooks: flat audio
  stream instead of the VideoApp HLS path
status: Done
assignee: []
created_date: '2026-09-14 20:08'
updated_date: '2026-09-15 05:48'
labels:
  - bug
  - audiobook
  - transport
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Audit 2026-09-14 (matrix intent x medium, code-verified): ResumeIntentHandler and StartOverIntentHandler route audiobooks through the FLAT audio endpoint (/Audio/{id}/stream via GetStreamUrl) instead of the VideoApp HLS concat path, because their launch predicate (BaseHandler.IsVideoAppLaunchItem, lines ~796-800: Movie/Episode/LiveTvChannel only) excludes AudioBook. PlayBookIntentHandler.cs:268 and LaunchRequestHandler.cs:251 route books to the VideoApp HLS path when NativeControlsForBooks is on; Resume and StartOver do not. Consequences: (a) Resume of a paused book restarts on the flat stream with NO sliced-resume seek bar (the offset rides as a plain start offset at best), breaking the NativeControlsForBooks experience the rest of the pipeline mandates; (b) StartOver of a book relaunches flat instead of through the HLS endpoint. FIX: add AudioBook (when NativeControlsForBooks on) to the launch predicate or the two handlers' gating so both ride the VideoApp HLS endpoint with the sliced-resume playlist (resume offset) and StartOver relaunches it from 0. Watch the StartOver special case: single-chapter books re-mint a chapter-scoped token (StreamHlsVideoAudioCore overrideToken) — the StartOver path must reuse the same entry point PlayBook uses, not hand-build URLs. Tests: handler-level pins for Resume/StartOver x (book, NativeControlsForBooks on/off). Audit cells: ResumeIntent AB=GAP, StartOverIntent AB=GAP.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED complete (2026-09-15). Resume and StartOver of audiobooks now ride the VideoApp HLS path under NativeControlsForBooks, gated at the callers (IsVideoAppLaunchItem untouched - the JF-499 W1 durable-fact split). The audit was half right, corrected by mutation checks: Resume fallback-4 really flat-launched (now the sliced ?start= playlist); StartOver was NOT flat (chokepoint already diverted it) - its real gaps were the stale tracker high-water mark (now cleared for AudioBook regardless of flag) and the silent launch (now explicit + progressive announce). The review's key discovery: the PRIMARY resume path is the session-held tail (FullNowPlayingItem never cleared after VideoApp launches) which restarted from 0; the shared TryBuildNativeControlsBookResumeAsync covers both entry points with a displaced-token guard. 10 tests, 3 mutation checks against pre-fix code; suite 3807/3807 both TFMs; Release 0 warnings. Gates: implementer-run 3-reviewer simplify + dedicated code-review high (0 P1, P2 ledger regression fixed centrally + pinned); every deferred item landed as JF-567 same-turn (bookKey/position chain migration, AudioBook idiom unification, PlayBook ShouldEndSession outlier, screenless chapter-selection, token+cleared-session corner). The VideoApp builders now record the last-played ledger - the foundation for JF-566.
<!-- SECTION:FINAL_SUMMARY:END -->
