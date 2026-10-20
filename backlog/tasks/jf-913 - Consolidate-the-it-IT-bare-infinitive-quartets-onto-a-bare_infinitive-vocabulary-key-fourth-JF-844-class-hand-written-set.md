---
id: JF-913
title: >-
  Consolidate the it-IT bare-infinitive quartets onto a bare_infinitive
  vocabulary key (fourth JF-844-class hand-written set)
status: To Do
assignee: []
created_date: '2026-10-20 05:41'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
  - scripts/generate_interaction_model.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The JF-844 wrapper-remainder class (the trainer does not generalize the unanchored bare-infinitive shape, so each condemned family needs literal bare-infinitive rows) is now on its FOURTH hand-written quartet in templates/it-IT.yaml: PlaySong (riprodurre/suonare/mettere/ascoltare {song}, 2026-09-24), PlayEpisode numbers-first and series-first (JF-844/JF-869), and PlayArtistSongs "riprodurre/suonare/mettere/ascoltare musica {artist_article} {musician}" (2026-10-20, device corr 15656cee). Every site re-documents the same rule in a comment: four verbs, pleiare excluded ("pleiare rides only the vocabulary products").

Consolidate: add a vocabulary key `bare_infinitive: [riprodurre, suonare, mettere, ascoltare]` to the it-IT template header (with the pleiare-exclusion rationale documented ONCE at the key) and convert the four music-verb quartets to single template rows (e.g. "{bare_infinitive} musica {artist_article} {musician}"). Verified feasible byte-identically: the generator expands refs via itertools.product in template-ref order, so the two-ref PlayArtistSongs product emits exactly the 16 committed samples in the committed order, and the single-ref quartets match their list order.

CONSTRAINT: this is a YAML-only refactor; the regenerated model_it-IT.json must be BYTE-IDENTICAL (empty git diff on the JSON) so no trainer statistics shift (the JF-541 discipline). The film/series families (mettere/guardare/vedere il film; riprodurre/mettere/guardare la serie) use different verb sets and stay literal; note that at the key.
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
