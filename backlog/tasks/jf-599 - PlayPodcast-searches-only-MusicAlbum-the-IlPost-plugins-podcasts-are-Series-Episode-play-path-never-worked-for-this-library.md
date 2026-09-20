---
id: JF-599
title: >-
  PlayPodcast searches only MusicAlbum; the IlPost plugin's podcasts are
  Series/Episode (play path never worked for this library)
status: Done
assignee: []
created_date: '2026-09-20 16:51'
updated_date: '2026-09-20 18:30'
labels:
  - bug
  - podcast
  - ilpost
milestone: Polish
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live-found 2026-09-20 (user test 2): PlayPodcastIntent searches MusicAlbum only, but the IlPost plugin stores podcasts as Series + Episode (78 series, 677 episodes under /data/media/podcasts, synced by the IlPost plugin; TotalRecordCount for MusicAlbum in that library = 0). The JF-373 model assumption covered the 3 community plugins (MusicAlbum of Audio) but not IlPost's shape. Result: the podcast play path NEVER worked for this library, even with a perfect slot fill (simulator-verified: podcast_name=generazione -> 'Non ho trovato un podcast chiamato generazione'). Fix: extend the handler's search to also query Series whose episodes are audio (or query Series then play the newest Episode child, mirroring the existing newest-Audio-child logic).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Simulator: 'riproduci il podcast generazione' (it-IT, IlPost library of Series/Episode items) PLAYS the newest episode instead of NotFoundPodcast
- [ ] #2 The MusicAlbum path still works for community-plugin libraries (query unchanged)
- [ ] #3 Suite green both TFMs
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit "fix(podcast): JF-599 the Series/Episode storage shape plays at last"). The IlPost library (78 Series / 677 Episode items / zero MusicAlbums, CollectionType=tvshows) now resolves: MusicAlbum query first (community-plugin path byte-identical, locked by test), Series fallback on miss, newest episode via the shared PodcastEpisodeResolver (AncestorIds + Episode|Audio + Limit 1 + IsVirtualItem=false for the series shape). Review round found and fixed the dead confirm paths: YesIntentHandler gained the podcast arm (was falling to MediaNotFound) and the APL carousel tap resolves a tapped Series (was FolderNoPlayableContent via the ParentId+MediaTypes=Audio child query). All three launch sites (handler, yes, APL) ride ResolveAudioLaunchSource so a name-matched TV episode can never dead-launch on eac3. TV-series contamination risk documented on the fallback (no type-level discriminator exists). 7 new tests; suite 4181/4181 both TFMs; /simplify + code-review gates passed with findings applied.
<!-- SECTION:FINAL_SUMMARY:END -->

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
