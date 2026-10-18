---
id: JF-907
title: >-
  fr wrapper-tail rows don't match the JF-900 fallback composition (lire vs
  jouer): the smoke fallback one-shots misroute (artist stolen by
  QueryArtistLibrary, favorites/film Fallback)
status: In Progress
assignee: []
created_date: '2026-10-18 21:21'
updated_date: '2026-10-18 21:21'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GOAL-DRIVEN (maintainer /goal 2026-10-18: 'verifica il Francese, i comandi devono funzionare ed essere propriamente verificati'). Model-side findings (profile-nlu, live saved model): the BARE fr forms route 7/7 clean (artist/favorites/song/random-film/video/album/playlist, verified against fixtures/fr-FR.yaml), but the WRAPPER-composed one-shots MISROUTE: 'demande à mon serveur de lire les chansons de pink floyd' -> QueryArtistLibraryIntent (steal), 'de lire mes favoris' -> AMAZON.FallbackIntent, 'de lire le film barbie' -> AMAZON.FallbackIntent. ROOT CAUSE (template inspection): the fr template's De-infinitive wrapper tails use JOUER and narrow carriers ('De jouer le film {title}', 'De jouer les {musician}') while the e2e smoke fallback (JF-900 compose_fallback_one_shot) composes with the infinitive of the BARE fixture verb: lis->lire, so the battery sends 'de LIRE les chansons de X' / 'de LIRE mes favoris' / 'de METTRE le film X' - none of which have rows. THE FIX: extend templates/fr-FR.yaml wrapper-tail families with the LIRE twins matching the JF-900 composition exactly: 'De lire les chansons de {musician}' (PlayArtistSongs), 'De lire mes favoris'/'De lire les favoris' (PlayFavorites - check which carrier the fixture uses), 'De lire la chanson {song}' (PlaySong), 'De mettre le film {title}' (PlayMovie - the bare fixture is 'mets le film barbie'), plus 'De jouer un morceau aleatoire' family check for PlayRandom (probe first: 'de jouer un morceau aléatoire' routed PlayRandomIntent fine, may need no row). Probe-first discipline: before adding each row, profile-nlu the exact composed form and only fix what misroutes. Keep the fixture pins for the wrapper forms added (tests/integration/fixtures/fr-FR.yaml or e2e wrapper pins), regenerate model_fr-FR.json, validators at baseline. ALSO verify fr-CA: its fallback composes identically (same map) - probe its wrapper forms and mirror rows if its template diverges. POST-MERGE: the orchestrator rebuilds fr-FR+fr-CA and re-probes; the goal's DoD closes with the e2e fr battery green (the orchestrator runs it when the router rate clears, >=3/5).
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
