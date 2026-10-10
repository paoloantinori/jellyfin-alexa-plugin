---
id: JF-861
title: >-
  AnnouncePositionOnResume bypass on the book native-controls arm: gate the
  announce like its siblings, or pin it as intentionally unconditional
status: To Do
assignee: []
created_date: '2026-10-10 20:09'
labels:
  - playback
  - ux
  - decision
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the JF-860 Part A completion round (review finding F2, real and pre-existing; the landed policy pin currently CEMENTS the existing shape, so the decision must be conscious).

The native-controls book resume (ResumeIntentHandler.TryBuildNativeControlsBookResumeAsync) speaks NowPlayingWithPosition WITHOUT the AnnouncePositionOnResume gate, while the flat-resume sibling (~line 374) and the resume offer both honor the flag. JF-860 Part A's new two-row handler pin plants flag=false and asserts the book announce STILL fires - so the bypass is now pinned behavior, and either branch of the decision changes that pin deliberately.

THE DECISION (maintainer's): (a) gate the book arm's announce on AnnouncePositionOnResume like its siblings (a behavior change: the position line stops announcing when the flag is off; the pin flips), or (b) document the book arm's announce as INTENTIONALLY unconditional (the position information may matter more on a deep book resume than the flag's economics; the pin stays with a rationale comment). Either is fine; silence is what this task forbids.
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
