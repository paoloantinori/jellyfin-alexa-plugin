---
id: JF-553
title: >-
  de-DE 'spiele staffel eins folge drei von breaking bad' stopped filling
  episode_number (stable, post JF-549-samples + post catalog-sync content
  change); bisect which change caused it
status: To Do
assignee: []
created_date: '2026-09-12 22:51'
updated_date: '2026-09-12 23:11'
labels:
  - bug
  - nlu
  - interaction-model
  - regression
dependencies: []
references:
  - JF-549
  - JF-552
  - JF-541
  - tests/integration/fixtures/de-DE.yaml
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found 2026-09-13 00:50 during the JF-549 post-deploy NLU run (stable across re-runs). The PRE-EXISTING de-DE fixture row 'spiele staffel eins folge drei von breaking bad' (expecting PlayEpisodeIntent with series_name/season_number/episode_number filled) now fails: intent still matches PlayEpisodeIntent but episode_number resolves EMPTY.

Timeline evidence:
- 2026-09-12 ~14:00 (JF-400 close-out): the row was GREEN on the then-live catalog-wired model.
- 2026-09-13 00:37: the JF-549 rebuild PUT the embedded de-DE model (adds 'Zu spielen {series} staffel {s} folge {e}' + 'Zu schauen {series} staffel {s} folge {e}'; static-seed types).
- 2026-09-13 ~00:45: the forced catalog sync re-PUT de-DE with catalog wiring + the JF-541 seed enrichment (Artist +2, Album +9; Series disabled this lifetime per JF-541b) - content differs from the last sync.
- 00:50 and re-runs: row RED (episode_number empty), while the it-IT tail-form control ('Riproduci la stagione uno episodio tre di breaking bad') stays GREEN, so the episode region is not broken wholesale.

Two candidate causes to bisect: (a) the two new Zu-samples sharing the staffel/folge anchors shifting number alignment for the imperative series-last form; (b) the catalog content change (JellyfinArtist values) shifting NLU priors. Related observation on the same night: the it-IT bare-infinitive series-first form fills series_name under the STATIC seed but drops it under the CATALOG wiring ('riprodurre breaking bad stagione uno episodio tre' filled at 00:33 static, empty at 01:00 wired) - catalog-wiring-dependent slot-fill variance is also the JF-552 evidence thread; keep the two tasks cross-referenced.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Bisect identifies whether the de-DE episode_number drop is caused by the JF-549 'Zu spielen/Zu schauen {series} staffel {s} folge {e}' samples or by the 2026-09-13 catalog-sync content change (JF-541 enrichment Artist +2/Album +9), by probing the string against a model variant with only one of the two changed
- [ ] #2 Whatever the cause: either the model is corrected so 'spiele staffel eins folge drei von breaking bad' fills all three slots again, or the fixture row is re-pinned with a skip_reason documenting the accepted new routing with evidence
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
STABILITY CORRECTION (2026-09-13 01:12-01:15): NOT stable-red - INTERMITTENT. Direct profile-nlu probe at 01:12 (post-sync-completion): episode_number still None. In-suite run 3 minutes later (01:15): PASSED with all slots filled. Red-red-green across 4 observations over 25 minutes on two different model states (post-rebuild static, post-sync wired). The bisect in AC#1 stands, but weight NLU nondeterminism as a third candidate: borderline number-slot alignment on the series-last imperative form that flips per invocation.
<!-- SECTION:NOTES:END -->

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
