---
id: JF-579
title: >-
  Adopt queue rehydration in AplUserEventHandler HandleNext/HandlePrevious (the
  same wiped-queue shape JF-577 fixed in the intent handlers) + consider an
  adoption-roster test
status: To Do
assignee: []
created_date: '2026-09-16 14:17'
labels:
  - reliability
  - queue
  - coverage
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-577 /simplify altitude pass (2026-09-16): AplUserEventHandler.HandleNext (~lines 116-138) and HandlePrevious (~lines 143-166) are line-for-line the same queue-scan shape the JF-577 adoption fixed in Next/PreviousIntentHandler, including the `Count == 0 || FullNowPlayingItem == null` bail, and they did NOT adopt the ProgressReporter.TryRehydrateSessionQueueFromDevice guard. After a mid-playback restart, an APL NowPlaying-screen next/previous tap would speak/answer from the wiped (empty) session queue while music plays. QueueRehydrationAdoptionTests pins only the three intent adopters and is behavioral, so a forgotten consumer fails silently. Scope when picked: read whether the APL tap request carries context.AudioPlayer (the tap fires while a stream plays, so it should), add the guard + ResolveCurrentItemId adoption in both APL branches, red-first tests in QueueRehydrationAdoptionTests style; if the APL context genuinely lacks the playing token, document why and close. Also consider a coverage-roster test (the WarmingGateCoverageTests IL-scan precedent) listing every session.NowPlayingQueue reader and its adoption status, so the next consumer cannot be forgotten silently. Related: JF-574, JF-577, JF-578.
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
