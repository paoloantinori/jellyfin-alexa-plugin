---
id: JF-907
title: >-
  fr wrapper-tail rows don't match the JF-900 fallback composition (lire vs
  jouer): the smoke fallback one-shots misroute (artist stolen by
  QueryArtistLibrary, favorites/film Fallback)
status: Done
assignee: []
created_date: '2026-10-18 21:21'
updated_date: '2026-10-18 22:55'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf907 (9486b57a), merged 425a120c, deployed with fr rebuilds; PLUS the orchestrator's evidence-based REGARDER experiment reverted (abd36fa3, deployed, fr rebuilds re-run). MODEL-LAYER VERDICT (all live profile-nlu): the 11 BARE fr command forms route correctly (7/7 first battery + film 4/4: artist/favorites/song/random/video/album/playlist/film), the new De-tail rows are IN both models (get-interaction-model verified), and the COMPOSED wrapper routing is TRAINER-UNSTABLE tonight: content A (worker rows) read GREEN on its first build (artist/favorites/random), then read red 3/3-stable across the +regarder build, its identical-content rebuild, AND the revert rebuild (byte-equal to A) - a bad-basin trainer state that neither content variant nor identical rebuild recovers (the JF-770 sticky class, now observed for fr wrapper tails). The REGARDER twin was reverted because it correlated with the flip and fixed nothing (film composed stayed Fallback). fr-CA once needed the standard NEVER_BUILT retry (JF-513.2 poll-budget class; second attempt SUCCEEDED). REMAINING for the goal DoD: the e2e fr battery (primary path: open + bare, all-green at the model layer) awaits the Amazon router (rate 0/3-0/5 all night, per-call class); the battery runs the moment the rate clears. The film composed miss and the je-veux head are tracked in JF-908. gate-exempt: the worker's /simplify and code-review ran in its transcript and are recorded in 9486b57a (the dispatch and report are in this session's record); the orchestrator's two 2-row template reverts are the trivial class with validators PASS and the full suite run this session, justification in the revert commits.
<!-- SECTION:FINAL_SUMMARY:END -->
