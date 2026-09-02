---
id: JF-443
title: >-
  JF-427 track-count sweep materializes the artist's full catalog on the hot
  path (1,533 rows for a 107-album artist) + raw-tag name matching zero-counts
  well-formed albums
status: In Progress
assignee: []
created_date: '2026-09-01 22:27'
updated_date: '2026-09-02 22:00'
labels:
  - code-review
  - efficiency
  - playback-quality
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:616
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:621
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Efficiency finding from the JF-427 review (2026-09-02, measured live on minix): GetAlbumTrackCountsAsync materializes every Audio BaseItem in the artist's ENTIRE catalog just to count them, on every indefinite album-by-artist request, inside the Alexa response window under RetryAsync. Live numbers: 'un disco dei Dave Matthews Band' (107 albums) deserializes 1,533 Audio objects; the Count<=1 elision only covers single-album artists (486 of 674 on this library) - exactly the cases that were already deterministic. Line 398 then re-fetches the chosen album's tracks (rows the sweep already fetched, plus the JF-338 fallback probe). Also flagged: counts are keyed by raw t.Album tag name and looked up by MusicAlbum.Name with only OrdinalIgnoreCase - trailing spaces, multi-disc tagging, accent variants zero out well-formed albums and flip the pick to any exactly-matching release.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Replace GetAlbumTrackCountsAsync's full-catalog materialization with COUNT-only queries (SafeGetItemsResult, AlbumIds=[album.Id], IncludeItemTypes=Audio, Limit=0, read TotalRecordCount - the pattern Jellyfin's own Folder.GetChildCount uses; identical AlbumIds semantics), keying counts by album ID (also removes the raw-Album-tag name-matching edge: trailing spaces/accent variants/'Name (Disc 1)' tagging currently zero out well-formed albums)
- [ ] #2 Cap the candidates ranked to the top-K (e.g. 12) by the existing deterministic order for the 100+-album tail
- [ ] #3 Alternatively adopt review Alternative B (carry the chosen album's pre-fetched track list to the queue build, eliminating the GetAlbumTracks re-query); either alternative removes the other
- [ ] #4 Unit tests keep the deterministic-pick guarantees (6-permutation test stays green)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
review-local gate (score 75): the per-album COUNT loop runs up to 12 sequential RetryAsync calls each carrying an INDEPENDENT 6s budget (fresh stopwatch per invocation); CLAUDE.md documents the per-call budget as the only Alexa-window guard, so the worst case is 12x6s on transient failures and slow-but-successful COUNTs (12x700ms) can exceed the ~8s window with no trip-wire. Task-sanctioned shape (AC#1 mandates per-album COUNT, sequential by design against SQLite contention); candidate hardening: a shared deadline across the loop (stop counting when elapsed > ~2s and rank the remainder deterministically).
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
