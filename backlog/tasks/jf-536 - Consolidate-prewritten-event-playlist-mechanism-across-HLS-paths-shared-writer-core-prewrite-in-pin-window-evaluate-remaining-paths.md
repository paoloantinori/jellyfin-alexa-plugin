---
id: JF-536
title: >-
  Consolidate prewritten event-playlist mechanism across HLS paths (shared
  writer core, prewrite in pin window, evaluate remaining paths)
status: To Do
assignee: []
created_date: '2026-09-10 06:51'
updated_date: '2026-09-10 07:18'
labels:
  - refactor
  - hls
  - video
  - tech-debt
  - consolidation
dependencies: []
references:
  - JF-531
  - JF-382 precedent
  - JF-428
  - VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-531 /simplify altitude review (2026-09-10): the prewritten event-playlist mechanism now has TWO copies (audiobook: WriteAudiobookPlaylist + inline serve in StreamHlsAudiobook; episode: WriteEpisodePlaylist + TryServePrewrittenEpisodePlaylist in StreamHlsEpisodeCore), and the codebase has FOUR HLS encode paths, two of which have NO prewrite at all:
1. StreamHlsAudiobook (prewrites; original)
2. StreamHlsEpisodeCore (prewrites; JF-531)
3. StreamHlsVideoAudioCore - serves songs AND single-chapter audiobooks via VideoApp, potentially HOURS long, zero prewrite, identical live-edge exposure class. The likely next copy-paste (would need seg_%03d naming, another divergence axis).
4. StreamHlsEpisodeAudioCore - audio-only episode variant consumed by AudioPlayer (no seek bar); whether AudioPlayer exhibits live-edge at all is UNKNOWN - evaluate with evidence before adding.

GATED on JF-531 AC#2 passing on device (extract before the device proof risks extracting the wrong shape). Scope when unblocked: (a) shared writer core (~12 identical emission lines: header block, token suffix, EXTINF F6 format, seg line, no-ENDLIST policy) parameterized by segment format, target duration, durations, discontinuity flag - consumed by both writers; (b) adopt the shared playlist file-name constant in the audiobook path (it hardcodes "playlist-full.m3u8" at ~:1292 and ~:1405 while the episode path has a named constant); (c) evidence-gated evaluation of prewrite for paths 3 and 4; (d) move the prewrite INSIDE the JF-428 pin window (both existing paths write the file BEFORE StartFfmpegProcessGatedAsync pins the entry; a concurrent eviction sweep in that window deletes the listing and the serve silently reverts to the live playlist - the JF-428 creation-to-pin class, replicated from the audiobook path). Deliberately NOT unified in JF-531: the serve layers differ on real axes (audiobook resume slicing via ServeAudiobookPlaylistAsync + 503-on-missing vs episode live-playlist fallback; single-point vs per-branch flag gating) - a unified helper would carry more parameters than the lines it saves.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Code-review gate note (2026-09-10, JF-531): when the shared writer core is extracted, carry the invariant-culture EXTINF fix to the audiobook writer too - WriteAudiobookPlaylist (~:2868) still formats {segmentDuration:F6} with current culture; the episode writer got string.Create(InvariantCulture, ...) in JF-531 but the twin carries the latent pattern (comma-decimal host cultures would render '3,999346,' and parsers read duration 3).
<!-- SECTION:NOTES:END -->
