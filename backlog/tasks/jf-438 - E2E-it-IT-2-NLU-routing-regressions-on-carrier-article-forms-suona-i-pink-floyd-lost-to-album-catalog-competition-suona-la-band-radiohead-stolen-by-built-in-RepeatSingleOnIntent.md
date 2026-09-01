---
id: JF-438
title: >-
  E2E it-IT: 2 NLU routing regressions on carrier/article forms ('suona i pink
  floyd' lost to album-catalog competition; 'suona la band radiohead' stolen by
  built-in RepeatSingleOnIntent)
status: To Do
assignee: []
created_date: '2026-09-01 12:47'
labels:
  - nlu
  - it-IT
  - e2e-finding
dependencies: []
references:
  - tests/integration/fixtures/e2e_it-IT.yaml
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - >-
    backlog/tasks/jf-436 -
    JF-418-bare-form-samples-compete-with-PlayVideoIntent-on-4-of-5-imperative-verbs-video-regression-direction-untested.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Full E2E battery against the deployed instance (2026-09-01, post JF-420.2/420.3/421 bundle): 54/56 passed. The 2 failures are NLU-routing-layer (reproduced at profile-nlu against the SAVED model, independent of the DLL - today's bundle changed no interaction models):

1. 'suona i pink floyd' (the JF-418 nominative-article form): NO selectedIntent; considered=[PlayAlbumIntent with the album slot ER_SUCCESS_MATCH-resolving to a catalog album literally named 'Pink Floyd', PlayArtistSongsIntent]. The AlbumName catalog (re-synced on every restart - 5 restarts today) appears to compete away the artist form.

2. 'suona la band radiohead' (band carrier, documented in the E2E matrix): selectedIntent=RepeatSingleOnIntent (a BUILT-IN intent, not in our model); considered=[PlaySongIntent]. Same carrier-competition family as the documented 'suona i radio*' -> PlayRadioIntent issue, different thief.

Handler layer fully green in the same run: simulator suite 8/8, E2E 54/56 including the whole artist-search matrix (coughing/pink/led zep/beatles/xyzzyfoo/gruppo/cantante).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce at profile-nlu level (both already confirmed 2026-09-01): 'suona i pink floyd' -> NO selectedIntent, considered=[PlayAlbumIntent (album slot resolves to the CATALOG album literally named 'Pink Floyd', ER_SUCCESS_MATCH), PlayArtistSongsIntent]; 'suona la band radiohead' -> selectedIntent=RepeatSingleOnIntent (built-in), considered=[PlaySongIntent]
- [ ] #2 Decide per failure whether it is catalog-entity competition (the AlbumName catalog containing a 'Pink Floyd' compilation steals the article form from PlayArtistSongs; catalogs re-synced on every restart, 5x on 2026-09-01) or Amazon NLU drift without model change, and fix accordingly: e.g. disambiguating samples for the article form, carrier-word hardening for 'la band', or catalog-slot reconsideration (cross-ref JF-415's musician-canonicalization family and JF-436's bare-form competition)
- [ ] #3 Add both utterances to the NLU fixture (tests/integration/fixtures/it-IT.yaml) so the model layer is covered by run_nlu_tests.sh, not only by the 22-minute E2E suite
- [ ] #4 E2E e2e_it-IT: both utterances route to PlayArtistSongsIntent again (56/56)
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
