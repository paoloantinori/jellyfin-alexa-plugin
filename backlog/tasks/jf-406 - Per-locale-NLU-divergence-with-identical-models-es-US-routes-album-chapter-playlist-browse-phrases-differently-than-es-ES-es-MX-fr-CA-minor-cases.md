---
id: JF-406
title: >-
  Per-locale NLU divergence with identical models: es-US routes
  album/chapter/playlist/browse phrases differently than es-ES (es-MX, fr-CA
  minor cases)
status: To Do
assignee: []
created_date: '2026-08-23 10:30'
labels:
  - nlu
  - localization
  - interaction-model
milestone: m-16
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found 2026-08-23 while extending NLU fixtures (JF-400). With IDENTICAL committed models (verified: deployed es-US == repo, 384 samples; es-MX/es-ES samples byte-equal), Amazon's per-locale NLU base models route the same Spanish utterances differently:

- es-US (worst, ~20 divergences): 'Reproduce el álbum thriller de michael jackson' -> PlaySongIntent (song='el album thriller') on es-US vs PlayAlbumIntent on es-ES; chapters ('Ir al capítulo cinco'), playlists, browse ('muéstrame álbumes'), mood ('reproduce música alegre') all diverge or fall to FallbackIntent.
- es-MX: 'muéstrame álbumes' -> AMAZON.FallbackIntent (BrowseLibraryIntent on es-ES).
- fr-CA vs fr-FR: 'Lis la musique de the beatles' -> PlayAlbumIntent (fr-CA) vs PlaySongIntent (fr-FR).

The es-US fixture was REVERTED (don't ship 20 divergent expectations); es-MX/fr-CA ship with the divergent cases annotated/adjusted to verified reality. Investigation options: per-locale sample tuning (add locale-specific variants where the base model competes differently), or accept divergence and document. Needs systematic per-locale profile-nlu comparison; also add retry-on-5xx to the NLU test client (SMAPI flakiness observed: single 500s fail cases that pass in isolation).
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
