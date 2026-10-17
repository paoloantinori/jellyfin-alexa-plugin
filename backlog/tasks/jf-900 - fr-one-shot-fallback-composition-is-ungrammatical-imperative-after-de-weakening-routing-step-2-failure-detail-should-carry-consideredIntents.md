---
id: JF-900
title: >-
  fr one-shot fallback composition is ungrammatical (imperative after 'de'),
  weakening routing; step-2 failure detail should carry consideredIntents
status: To Do
assignee: []
created_date: '2026-10-17 10:08'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live evidence 2026-10-17 (the JF-899 validation run): the smoke open fallback fires correctly on fr-FR, but the fallback one-shot itself lost selection with the no-intent marker. The composed utterance is 'demande à mon serveur de lis les chansons de pink floyd' - the French wrapper 'demande à X de ' wants the INFINITIVE (de lire), while the fixtures' bare commands are imperatives (lis, joue, mets, ferme), so every fr fallback composes ungrammatical French. The same morning, a manual one-shot with the infinitive ('demande à mon serveur de lire les chansons de pink floyd') ROUTED to the skill (it picked QueryArtistLibraryIntent instead of the play intent: a separate model-quality wart, see the 2026-10-17 research point 5). SCOPE: (1) capture consideredIntents in the step-2 one-shot failure detail (today the no-intent marker cannot distinguish selection-loss from NLU-no-match on that path); (2) add a fr one-shot composition that infinitivizes the leading verb (a small fr verb map: lis->lire, joue->jouer, mets->mettre, ferme->fermer, arrete->arreter, ouvre->ouvrir...) or per-fixture one-shot overrides, the JF-551 es-subjunctive family pattern; (3) the QueryArtistLibraryIntent steal on the fr infinitive play one-shot, if it persists after (2), is the next model-side row. PIN: a fr fallback one-shot with the infinitive composition routes to the skill.
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
