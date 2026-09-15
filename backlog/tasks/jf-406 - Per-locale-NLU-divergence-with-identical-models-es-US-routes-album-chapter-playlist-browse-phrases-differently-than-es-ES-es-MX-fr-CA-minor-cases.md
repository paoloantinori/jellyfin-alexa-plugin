---
id: JF-406
title: >-
  Per-locale NLU divergence with identical models: es-US routes
  album/chapter/playlist/browse phrases differently than es-ES (es-MX, fr-CA
  minor cases)
status: In Progress
assignee: []
created_date: '2026-08-23 10:30'
updated_date: '2026-09-15 11:04'
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
NEW DATA POINT (2026-08-29, JF-414 verification): es-US 'Reproduce un album de queen' routes to PlaySongIntent (song='1 album') while the bare 'un album de queen' routes correctly to PlayAlbumIntent on the same model - and the identical imperative shape works on es-ES/es-MX (their siblings use the same vocabulary). Same class as the album/chapter/playlist cases already tracked here: identical committed models, divergent per-locale NLU. Fixture asserts only the working bare form; the imperative divergence is documented in tests/integration/fixtures/es-US.yaml.

SYSTEMATIC PROBE COMPLETE (profile-nlu, 2026-09-15, skill 33dfacd5, post RepeatIntent rebuild; every divergent cell re-probed 3x, all stable; 5xx retry added to tests/integration/smapi_client.py first). es-ES truth vs siblings: 'Reproduce el álbum thriller de michael jackson' -> PlayAlbumIntent on es-ES AND es-MX, PlaySongIntent (song='el album thriller michael jackson') on es-US. 'Ir al capítulo cinco' -> GoToChapterIntent(5) on es-ES/es-MX, PlaySongIntent (song='al capitulo 5') on es-US. 'reproduce música alegre' -> PlayMoodMusicIntent(alegre) on es-ES/es-MX, PlaySongIntent (song='musica alegre') on es-US. 'muéstrame álbumes' -> BrowseLibraryIntent on es-ES, AMAZON.FallbackIntent on BOTH es-MX and es-US. 'Reproduce un álbum de queen' (accented) -> PlayAlbumIntent on es-ES/es-MX, PlaySongIntent (song='1 album') on es-US; bare 'un álbum de queen' -> PlayAlbumIntent on all three. fr: 'Lis la musique de the beatles' CONVERGED - fr-CA and fr-FR both now route PlaySongIntent (musician='the beatles', song='la musique').

DIFF VERDICT: es-US's committed model is NOT byte-identical to es-ES/es-MX (its template legitimately drops 3 leading BrowseLibrary samples plus FollowMe/PlayPlaylist/Recommend samples, JF-316 golden master of the hand-grown model) - but every DIVERGENT family's samples are byte-identical across the three es locales, and es-MX (full sample set) fails 'muéstrame álbumes' exactly like es-US, so the missing samples are not causal. All four divergent es-US families plus the es-MX browse case are Amazon per-locale BASE-MODEL arbitration with our model sample-identical; the fr-CA album case was base-model too and has since healed on Amazon's side.

DECISION: acceptance + documentation, no sample tuning. The failing carriers ('Reproduce el álbum {album} de {musician}', 'Ir al capítulo {chapter_number}', 'reproduce música {mood}') are ALREADY verbatim samples of the correct intents; adding more samples could only be verified by deploying to SMAPI (not done, per the deploy-freeze instruction), which would make any edit a guess. Fixtures updated as verified-reality documentation only: es-US.yaml carries the full 4-family divergence record with the 2026-09-15 probe data; fr-CA.yaml's stale comment updated to note the convergence; es-MX.yaml's existing note re-verified accurate. Residual risk if ever revisited: candidate es-US tuning would be extra noun-redundant album carriers, testable only in the next live deploy window.

INFRA (from this task's own scope): retry-on-5xx added to tests/integration/smapi_client.py (SmapiServerError classified by stderr markers in _run_ask, retried in profile_nlu alongside rate limits, same 3-attempt backoff); verified offline with mocked subprocess (transient-500 recovery + exhaustion after 3 attempts). NLU dry-run green: 899 fixture entries collect + schema-validate.
<!-- SECTION:NOTES:END -->
