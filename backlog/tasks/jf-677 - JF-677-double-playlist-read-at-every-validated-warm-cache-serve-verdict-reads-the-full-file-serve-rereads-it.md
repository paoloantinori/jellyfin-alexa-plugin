---
id: JF-677
title: >-
  JF-677 - double playlist read at every validated warm-cache serve: the
  verdict reads the full file, the serve re-reads it
status: To Do
assignee: []
created_date: '2026-09-30'
labels:
  - encode-gate
  - performance
dependencies: []
references:
  - >-
    backlog/tasks/jf-676 -
    JF-676-own-dead-foreign-live-serve-window-can-serve-a-killed-own-ticks-encodes-stale-no-ENDLIST-playlist-needs-a-ticks-scoped-debris-verdict.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-676 code-review round (high effort, finding 2 of 5; the JF-676 /simplify efficiency angle independently raised the same shape and skipped it as outside that diff).

THE COST: `ValidateHlsCacheAsync` (the ticks-scoped debris verdict core) reads the ENTIRE playlist into a string just to test `#EXT-X-ENDLIST` presence and (audiobook only) count `#EXTINF` lines, then discards the string; the immediately following serve (`ServePlaylistWithToken`/`ServeEpisodePlaylist`/`ServeAudiobookPlaylistAsync`) re-reads the same file, synchronously, for the token rewrite. Since JF-309 puts `?token=` on effectively every skill-served URL, the token branch (ReadAllText + rewrite) is the norm, so every validated warm-cache serve pays TWO full-file reads plus a transient string of the whole playlist. Bounded but real: an audiobook/episode playlist is ~3000 `#EXTINF` lines (~150KB) and the Echo re-fetches the playlist periodically during playback; the song path newly inherited the double read from JF-676's verdict (it previously served `cached.FullName` straight past the gate), while the episode/variant/audiobook paths have carried it since their verdicts were born.

FIX DIRECTIONS (either, or both):
1. Thread the already-read content through: the verdict returns `(FileInfo?, string?)` (file + content) and the serve helpers take an optional preloaded-content argument; one read per serve on every path, no new I/O shape.
2. Cheaper probe: decide ENDLIST presence (and the audiobook undercount) from a size-capped TAIL read (ENDLIST is the playlist's last line), keeping the serve as the only full read.

VERIFICATION: a pin is optional (behavior-identical); the proof burden is the read-count: assert (test seam or measured) that a warm-cache serve performs exactly one read of stream.m3u8. Full suite green both TFMs unchanged.
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
