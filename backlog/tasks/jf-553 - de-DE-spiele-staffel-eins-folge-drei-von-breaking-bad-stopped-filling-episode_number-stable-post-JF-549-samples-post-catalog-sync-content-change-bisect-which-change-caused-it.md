---
id: JF-553
title: >-
  de-DE 'spiele staffel eins folge drei von breaking bad' stopped filling
  episode_number (stable, post JF-549-samples + post catalog-sync content
  change); bisect which change caused it
status: Done
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

## Implementation Notes

BISECT COMPLETE 2026-09-13 (live A/B on the deployed skill, 10-probe batteries):
- (a) JF-549 Zu samples: REFUTED. The de-DE model was rebuilt WITHOUT all three Zu episode samples and the drop persisted unchanged (0/10 with vs 0/10 without; template restored after the experiment).
- (b) catalog wiring: CONTRIBUTING PRESSURE, NOT DETERMINATIVE. An UNWIRED de-DE model (embedded content via custom-URL deploy) filled episode_number 10/10, vs 0/10 on the wired model at the same moment. But after restoring the wired state via an equivalent rebuild, the SAME utterance filled again (1/1) - identical logical model content, opposite behavior.
- (c) VERDICT: Amazon NLU trainer nondeterminism across rebuilds of identical content. The "red-red-green" pattern of 2026-09-13 00:50-01:15 was this same flip, not a code regression. Wiring density (1133 JellyfinArtist values) widens the instability band; the unwired model was the only consistently-green state. The stable verb is "schau" (fills in both states); "spiele" flips (competition with PlaySong's "spiele {song}" free-text carrier).
FIX APPLIED (AC#2 second disjunct): the fixture row re-pinned with the full verdict as its skip_reason. No model change (the Zu samples stay; refuted as the cause).

SIDE-FINDING FIXED IN-DIFF (filed nowhere else, tracked here): the bisect was initially BLOCKED because custom-model/deploy AND custom-model/restore both PUT EMPTY models (intents=0 samples=0 -> Amazon build FAILED; live evidence 2026-09-13 17:27/17:31). Root cause: CreateSkillInteractionModel deserialized the WRAPPED envelope into SkillInteraction, which binds the INNER model (languageModel/dialog). Fixed by unwrapping before deserialization; regression test CreateSkillInteractionModelTests covers both envelope shapes; verified live (the custom-URL deploy then PUT intents=60 samples=426 and built). This also un-broke the restore endpoint.

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
