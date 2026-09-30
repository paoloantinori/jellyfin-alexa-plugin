---
id: JF-686
title: >-
  JF-686 - single-chapter audiobook resume redirect drops ?start=, a resume
  launch on a one-chapter book restarts from 0:00
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - audiobooks
  - resume
dependencies: []
references:
  - >-
    backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-682 code-review round (high effort, finding 3 of 5); PRE-EXISTING gap, untouched by JF-682's diff (the review surfaced it inside the rewritten branch and JF-682's mandate was the empty-secret 503 only).

THE GAP: `PlaybackLaunchBuilder.BuildAudiobookResumeResponse` mints `audiobook/{parentId}/stream.m3u8?start=<ticks>` whenever the resolved position is positive (ResumeIntentHandler and YesIntentHandler both route through it), and `StreamHlsAudiobook` binds `startTicks` and slices the MULTI-chapter playlist via `ServeAudiobookPlaylistAsync`. But the SINGLE-CHAPTER branch calls `StreamHlsVideoAudioCore(chapterId, chapterToken)` and drops `startTicks`: the song core serves the full unsliced playlist and has no `?start=` handling of its own, so a "resume from 20 minutes" on a one-chapter book (or any book that resolves to a single child) plays from the beginning while the device's seek bar shows a fresh timeline. The user-visible shape is a resume that silently restarts, exactly the class the sliced-playlist mechanism (JF-499 W2) was built to remove for multi-chapter books.

FIX DIRECTION: thread the resume offset into the single-chapter serve the way the episode path does (`ServeEpisodePlaylistAsync` slices + serves): either a start-aware serve for the redirected core (slice the song-core playlist at the offset using the same `AudiobookPlaylistBuilder.BuildResumePlaylist` mechanics with the song core's segment duration) or route single-chapter books through the same slicing helper. Needs a red pin first (resume launch on a one-chapter book must serve a sliced playlist whose first EXTINF starts at/below the offset), plus a device check that the song core's 3-digit/10s segment geometry slices correctly.
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
