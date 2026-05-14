---
id: JF-124
title: Progressive queue building for faster time-to-audio
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 12:50'
labels:
  - enhancement
  - performance
  - playback
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Start playing the first track immediately while asynchronously fetching and queuing the rest of the album/artist catalog. This reduces time-to-first-audio for large libraries where fetching all items takes noticeable time.

Inspired by JellyMusic's two-stage approach: first batch processed immediately and returned to Alexa, remaining items processed asynchronously via a `then` callback.

Implementation: In bulk-play handlers (PlayAlbumIntent, PlayArtistSongsIntent, etc.), send the first track's AudioPlayer directive immediately, then queue remaining items via background processing. Use PlaybackNearlyFinished to dynamically enqueue as needed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 First track begins playing before full album/artist catalog is fetched and queued
- [ ] #2 Remaining items are queued asynchronously after first track starts
- [ ] #3 No visible delay between intent response and first audio
- [ ] #4 Works for play-by-artist, play-album, and similar bulk-play intents
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Two-stage progressive queue: first 5 items fetched immediately, remaining lazy-fetched via PlaybackNearlyFinished. QueueContinuation DTO + store + fetcher. 15 unit tests. Works with gapless playback and per-device queues.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
