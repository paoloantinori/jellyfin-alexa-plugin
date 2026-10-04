---
id: JF-757
title: >-
  JF-757 - the album-track query shape is hand-kept in four copies (head and tail x
  ParentId and AlbumIds); a BuildAlbumTracksQuery builder would own it
status: To Do
assignee: []
created_date: '2026-10-04'
labels: []
references:
  - backlog/tasks/jf-753 - JF-753-AlbumPlayService-first-page-has-the-JF-673-engagement-gap-on-NRE-class-servers-albums-truncate-at-the-initial-page.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-753 /simplify pass (2026-10-04, reuse angle observation, judged out of that diff's scope): the album-track query initializer exists as FOUR hand-kept copies that must stay in lockstep:

1. `AlbumPlayService.BuildAlbumPlayResponseAsync` first page, ParentId arm (`AlbumPlayService.cs` ~577)
2. Same method, AlbumIds retry arm (~599)
3. `QueueContinuationFetcher.FetchAlbumTracks` tail, ParentId arm (~168)
4. Same fetcher, AlbumIds retry arm (~203)

All four share User/Recursive/IncludeItemTypes=Audio/DtoOptions(true)/OrderBy=AlbumTrackOrder and differ ONLY in the scoping term (ParentId vs AlbumIds) and the paging (Limit=GetInitialFetchSize at the head vs StartIndex/BatchSize at the tail). The repo already codifies the fix pattern: `QueueContinuationFetcher.BuildAudiobookChaptersQuery` (JF-670) is the ONE shared builder the PlayBook head and the audiobook tail both run, with the doc explicitly warning that "a hand-kept copy in the handler would drift exactly like the stale-mirror class that bit the interaction-model docs (anti-pattern 11)". The album path never got the same builder; JF-753's own tail comment invokes the head/tail executor contract, and the four copies are the query-shape half of that same drift risk (the executor half was unified by JF-753: both ends now run SafeGetItemsResult).

FIX SHAPE: add `QueueContinuationFetcher.BuildAlbumTracksQuery(jellyfinUser, albumId, startIndex, limit, byAlbumIds)` (or two overloads) next to `BuildAudiobookChaptersQuery`; route all four sites through it. Behavior byte-identical; the JF-338 split-album retry keeps its two-arm shape, only the initializers collapse.

NOT DONE in JF-753 because the filing's assigned surface was the engagement/regime work (opt-in, gate, marking, consolidation of the advance-or-mark idiom and the renderer); rewriting pre-existing query initializers at all four sites is a separate, purely structural change that deserves its own review pass.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 /simplify passed (no blocking cleanups remaining)
- [ ] #5 /code-review high passed (no blocking findings remaining or findings applied/tracked)
- [ ] #6 A query-shape pin (head vs tail captured queries identical modulo paging) fails on a seeded divergence and passes on the builder
<!-- DOD:END -->
