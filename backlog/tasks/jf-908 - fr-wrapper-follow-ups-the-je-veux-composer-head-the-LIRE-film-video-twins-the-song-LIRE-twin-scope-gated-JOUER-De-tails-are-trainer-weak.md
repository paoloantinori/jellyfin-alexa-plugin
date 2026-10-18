---
id: JF-908
title: >-
  fr wrapper follow-ups: the je-veux composer head, the LIRE film/video twins,
  the song LIRE twin (scope-gated; JOUER De-tails are trainer-weak)
status: To Do
assignee: []
created_date: '2026-10-18 22:22'
updated_date: '2026-10-18 23:40'
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
