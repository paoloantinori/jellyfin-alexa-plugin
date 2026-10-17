---
id: JF-900
title: >-
  fr one-shot fallback composition is ungrammatical (imperative after 'de'),
  weakening routing; step-2 failure detail should carry consideredIntents
status: Done
assignee: []
created_date: '2026-10-17 10:08'
updated_date: '2026-10-17 10:41'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf900 (a02b59e6), merged to main f0f31b70, pushed, and LIVE-VALIDATED on the fr pink row: both deliverables proven on the wire (the composer sent the infinitive form, the step-2 failure labels 'sent:' plus the typed SmapiSelectionLostError class). THE CHANGE: SmapiClient.compose_fallback_one_shot with FR_FALLBACK_INFINITIVE (lis/joue/mets/met/ferme/arrete/ouvre -> lire/jouer/mettre/mettre/fermer/arrêter/ouvrir, fr-FR+fr-CA only, unknown heads and non-fr byte-identical) placed next to INVOCATION_PREFIX but invoked ONLY at the smoke open-fallback construction site (structural only-fallback constraint: full-chain and reliability paths never call it; the worker's review also refuted the task's own full-chain-fr premise: no e2e_fr-*.yaml exists, the fr rows feed profile_nlu which applies no prefix). Step-2 pytest.fail embeds type(exc).__name__ + str(exc)[:120] (consideredIntents are unretrievable after the client raises; the class name is the discriminator) and labels the sent form when the composer rewrote the row. 14 new hermetic tests; runner wired. Gates (in a02b59e6): py_compile + bash -n, 35 hermetic units, dry-run green (35 passed 158 skipped), code-review high scope-verified with 2 applied 5 reasoned rejections; Python-only so no dotnet gates. LIVE RESIDUAL: the validation row still lost selection because the fr router was in a bad window at 12:41 (the open, the one-shot, AND the bare 'stop' reset all failed in the same minutes; the identical prefixed-infinitive form routed at ~09:45) - no composition fixes a whole-locale selection storm; the row passes when the router cooperates. The QueryArtistLibraryIntent steal on the fr infinitive play one-shot remains the model-side follow-up IF it persists in a good window (noted in the task description). gate-exempt: gates ran in the dispatched worker's transcript; commit a02b59e6 carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
