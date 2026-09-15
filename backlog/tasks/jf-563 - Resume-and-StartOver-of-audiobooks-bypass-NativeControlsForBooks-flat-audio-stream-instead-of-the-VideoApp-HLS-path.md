---
id: JF-563
title: >-
  Resume and StartOver of audiobooks bypass NativeControlsForBooks: flat audio
  stream instead of the VideoApp HLS path
status: In Progress
assignee: []
created_date: '2026-09-14 20:08'
updated_date: '2026-09-15 04:30'
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
