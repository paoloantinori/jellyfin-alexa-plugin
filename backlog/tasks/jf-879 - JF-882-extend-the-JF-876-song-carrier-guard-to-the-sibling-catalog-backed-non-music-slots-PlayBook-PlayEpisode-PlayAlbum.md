---
id: JF-879
title: >-
  JF-882: extend the JF-876 song-carrier guard to the sibling catalog-backed
  non-music slots (PlayBook/PlayEpisode/PlayAlbum)
status: To Do
assignee: []
created_date: '2026-10-13 09:01'
updated_date: '2026-10-13 10:15'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual from JF-876's /simplify altitude review (2026-10-13): the song-carrier misroute guard (MultiValueErDisambiguation.TryDetectSongCarrier, promoted there for reuse) is wired only on PlayMovieIntent in PlayVideoIntentHandler. The same catalog-backed-ER-pull mechanism that caused the movie steal exists on SeriesName (PlayEpisode/PlayNextEpisode templates), AudiobookTitle (PlayBook, catalog-synced since JF-823), and AlbumName (PlayAlbum, it-IT): "metti il brano X" where X is also a series/audiobook/album can be pulled to those intents by the same ER ranking on a future trainer roll. The helper is already generic (raw slot value in, carrier-stripped song title out) and lives next to the shared strip table; wiring is per-handler entry delegation to PlaySongIntentHandler, mirroring the JF-876 shape (typed singleton + SynthesizeIntentRequest). Decide per-intent whether the guard belongs (a "brano"-carried album ask is plausibly the JF-345 cascade's job; verify the NLU steal is structurally possible per slot type before wiring).
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
JF-876 code-review F6 (2026-10-13): the same fill-swallow misroute class also stays open on PlayVideoIntent's free-text title slot served by the SAME handler (PlayVideoIntentHandler): a song ask NLU-steered to PlayVideoIntent carries title='il brano X', runs the video search on the unstripped fill, and answers NotFoundVideo. Cover it with the sibling wiring decision; note there is no ER canonical on that SearchQuery slot, so the JF-876 whole-span escape (fill == canonical) is unavailable there and the guard shape needs its own title-starts-with-carrier vs movie-title trade-off.
<!-- SECTION:NOTES:END -->
