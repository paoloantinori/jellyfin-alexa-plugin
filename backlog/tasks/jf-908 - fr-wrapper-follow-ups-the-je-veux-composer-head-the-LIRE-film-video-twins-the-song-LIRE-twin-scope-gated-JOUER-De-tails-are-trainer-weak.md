---
id: JF-908
title: >-
  fr wrapper follow-ups: the je-veux composer head, the LIRE film/video twins,
  the song LIRE twin (scope-gated; JOUER De-tails are trainer-weak)
status: Done
assignee: []
created_date: '2026-10-18 22:22'
updated_date: '2026-11-27 22:17'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-907 worker residuals (could not file from its worktree). THREE same-class items, LOW, decide together when taken: (1) COMPOSER GAP: 'demande a mon serveur de je veux regarder barbie' (the smoke battery's unchanged-head row per JF-900's safe passthrough) reads Fallback at the model layer - no grammatical sample can catch a 'de je veux' head; the fix is a COMPOSER follow-up (map je-veux heads to a natural tail, e.g. strip the head and compose 'de regarder barbie'), not a model row; the payload 'je veux regarder barbie' itself routes PlayVideo so only the composed one-shot misses. (2) The LIRE twins with no battery composition today (probed Fallback/NO_SELECTION both locales): 'de lire le film {title}' and 'de lire la video {title}' - the same user-facing class a reviewer flagged; add only if a battery row ever emits them (scope discipline). (3) 'De lire la chanson {song}' (PlaySong) deliberately not added in JF-907 (no smoke composition emits it); add alongside if the fr smoke set ever grows a song row. Context: the fr film fallback was fixed with the METTRE row + the REGARDER twin (this session); the JOUER-verb De-tail family is proven weak (its exact row loses) - prefer METTRE/REGARDER/LIRE verbs for any future fr De-tail row.
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
GOAL PROGRESS 2026-10-19 01:40: the HANDLER-layer fr battery ran green 6/6 via the built-in simulator on minix (artist/favorites/random/film/album/playlist: real pipelines, correct directives, French speech; the playlist not-found is correct behavior for absent data). Combined with the model layer (11/11 bare forms green, live profile-nlu), the fr commands are verified on every layer under our control. Still Amazon-gated: the simulate-skill e2e battery (router 0/3 all night, per-call class, opened 09:45 yesterday) and the wrapper composed forms (trainer bad basin, 6 builds, catalog hypothesis refuted). Retry cadence: every heartbeat.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED item-1-complete (2026-11-27, through the code-review round): (1) THE COMPOSER: compose_fallback_one_shot strips a polarity head (the SET: je veux / je voudrais / j'aimerais) when the next word is a known infinitive OR imperative key, so 'demande à ... de je veux regarder barbie' composes grammatically as 'de regarder barbie'; partitive noun-phrase rows (je veux de la musique relaxante) never strip (no doubled preposition); 20 tests pin the set, the guard, the fr-gating, and the word boundaries. (2) THE MODEL TWIN: 'De regarder {title}' landed in fr-FR AND fr-CA PlayVideoIntent. History honored: the twin was reverted once (abd36fa3, it flipped artist/favorites composed rows red and fixed nothing); re-landed on NEW lab evidence (run 20261127-230534): the trainer landscape changed since October (many De-tail rows landed since), the twin routes its form AND every sentinel stays green in the same build (de jouer des chansons/musique de queen -> PlayArtistSongs, lis mes favoris -> PlayFavorites, de mettre le film -> PlayVideo). Post-land lab verify: 'de regarder barbie' routes PlayVideoIntent in BOTH templates (runs 20261127-231401/231532, 0 exp failures). Fixtures: bare-tail twin rows added to both locales (red-until-deploy). (3) ITEMS 2-3 stay scope-deferred per the task's own discipline: no smoke row emits a LIRE film/video composition; the LIRE chanson twin likewise. LIVE DEPLOY of the two model rows: rides the next vai-gated rebuild (the stale-DLL lesson applies: DLL first, then rebuild). gate-exempt: test-tooling + model-data only (composer, templates, fixtures, mirrors); the code-review high round ran on the composer change (5 findings, all applied: the routing claim corrected with the live no_selection probe, the head set + partitive guard, the docstring contract fix, the hyphen fix, and this task record); composer suite 20/20, validators green, voice mirrors regenerated.
<!-- SECTION:FINAL_SUMMARY:END -->
