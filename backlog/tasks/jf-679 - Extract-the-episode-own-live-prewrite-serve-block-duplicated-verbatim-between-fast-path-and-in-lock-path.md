---
id: JF-679
title: >-
  JF-679 - extract the episode own-live prewrite-serve block duplicated
  verbatim between fast path and in-lock path
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - tech-debt
  - encode-gate
dependencies: []
references:
  - >-
    backlog/tasks/jf-677 -
    JF-677-double-playlist-read-at-every-validated-warm-cache-serve-verdict-reads-the-full-file-serve-rereads-it.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-677 code-review round (high effort, finding 5 of 5).

THE DUPLICATION: `StreamHlsEpisodeCore` carries the own-live prewrite-serve plus fresh-EPISODE-serve block twice, verbatim: once inside the fast path's `TryServeValidatedHlsCacheAsync` serve closure and once on the in-lock concurrent-serve row (the `OwnTicksGenerationLive(_activeEpisodeEncodes, ...)` check + `TryServePrewrittenEpisodePlaylist` + fall-through `ServeEpisodePlaylistAsync`). The SONG path (`StreamHlsVideoAudioCore`) carries the same shape duplicated at its two sites. JF-676 and then JF-677 each had to apply the identical edit to both copies (JF-676: the verdict threading; JF-677: the await conversion + Content threading); the next behavioral tweak to the own-live serve row (e.g. an audiobook-style cold-entry startTicks drop) applied to only one copy compiles clean and drifts silently between the fast and in-lock paths.

FIX DIRECTION: a `TryServePrewrittenEpisodePlaylist`-style helper owning the whole "prewrite if own-live (registry, key, ticks), else serve the validated playlist with (startTicks, content)" block, called from both sites (and the song twin's equivalent), the same consolidation pattern `TryServeValidatedHlsCacheAsync` already set for the verdict+serve wrapper. Needs the existing JF-675/JF-676 episode + song pins green after the extraction (they exercise both rows on both paths), plus a build check that the closures' capture sets stay correct.
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
