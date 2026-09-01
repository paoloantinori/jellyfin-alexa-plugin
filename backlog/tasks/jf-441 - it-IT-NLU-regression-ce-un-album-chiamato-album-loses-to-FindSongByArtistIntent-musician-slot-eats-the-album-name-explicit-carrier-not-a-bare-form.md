---
id: JF-441
title: >-
  it-IT NLU regression: 'c'e un album chiamato {album}' loses to
  FindSongByArtistIntent (musician slot eats the album name; explicit carrier,
  not a bare form)
status: To Do
assignee: []
created_date: '2026-09-01 21:09'
labels:
  - nlu
  - it-IT
  - e2e-finding
dependencies: []
references:
  - tests/integration/fixtures/it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
New finding from the JF-436 NLU suite run (2026-09-01, stable across 2 independent observations: suite + direct probe): the it-IT NLU fixture 'c'e un album chiamato dark side of the moon' FAILS - it now routes to FindSongByArtistIntent with musician='dark side of the moon' instead of PlayAlbumIntent's album slot. The fixture's own comment ('the album-specific pattern wins over PlayArtistSongsIntent') no longer holds against the deployed model. NOTE: this utterance has an explicit album carrier, NOT a bare form, so it is NOT a JF-418 artifact; during the same JF-436 probing session, PlayEpisodeIntent was observed pulling in via SeriesName catalog resolution on series titles, so catalog-entity weighting is a suspect. The fixture is currently left failing (existing-entries-untouched rule); triage = fixture update or model fix.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce at profile-nlu (already observed twice 2026-09-01, stable: suite + direct probe): 'c'e un album chiamato dark side of the moon' -> FindSongByArtistIntent musician='dark side of the moon' instead of the fixture's PlayAlbumIntent album slot
- [ ] #2 Diagnose: NOT a bare-form shape (has the explicit 'album chiamato' carrier) - suspects: the FindSongByArtist dialog samples competing, SeriesName/catalog entity pull (the same catalog resolution that pulled PlayEpisodeIntent on series titles during JF-436 probing), or JF-418-era drift
- [ ] #3 Fix (model samples or catalog review) + update the NLU fixture; green it-IT NLU suite
- [ ] #4 Sanity: sibling carriers ('un album chiamato X', 'l'album X') still route to PlayAlbumIntent
<!-- AC:END -->

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
