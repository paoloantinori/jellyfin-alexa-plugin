---
id: JF-597
title: >-
  Triage the 65 NLU failures of the recreated skill's trainer baseline (model
  fix vs fixture update, per case)
status: To Do
assignee: []
created_date: '2026-09-20 01:46'
updated_date: '2026-09-20 11:04'
labels:
  - nlu
  - interaction-model
  - tech-debt
milestone: Polish
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Post-recreation trainer baseline triage. The 2026-09-20 full NLU re-run (AFTER the catalog recovery + with ER verified alive via ER_SUCCESS_MATCH) shows the failures UNCHANGED: 65 failed / 987 passed vs 66/986 before - the catalog outage was real but separate. The failures are deterministic routing behaviors of the RECREATED skill (Sep-16 config wipe -> new skill -> new trainer state): the old fixtures pinned expectations that relied on the previous skill's trainer quirks/generalizations. Representative confirmed cases (probed 3x deterministic, survived an identical-content rebuild): 'Riproduci star wars' (it) -> PlayNextIntent song-steal; 'Trova/Find inception' (it/en) -> NO_SELECT; 'Suona abbey road' -> NO_SELECT; es-US album slot not filling; MediaInfo media_info_type empty. NOTE the memory nlu_trainer_nondeterminism: per-case fixes need their own 6-10 probe batteries before shipping.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Full failure list captured (pytest cache + /tmp/nlu_rerun_full.log, 2026-09-20 run: 65 failed / 987 passed / 22 skipped)
- [ ] #2 Each failure classified: (a) real user-facing misroute worth a model fix (candidate list from the probes: bare 'Riproduci star wars' class -> PlayNextIntent steal; search verbs 'Trova/Find X' -> NO_SELECT; 'Suona abbey road' bare album -> NO_SELECT) or (b) fixture expectation pinned to the pre-recreation trainer state (generalization-dependent), to update
- [ ] #3 For class (a): model-side fixes follow the anti-pattern rules (carriers/nouns, no bare greedy samples); for class (b): fixtures updated with a comment naming the trainer re-roll
- [ ] #4 Full NLU re-run after the batch: failures reduced to the agreed residual, all remaining failures documented as accepted
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-20 triage round 1 (live probes, 2-3x each, all deterministic): CLASSIFICATION of the 65 failures into four classes. (A) TRANSIENT, already healed: the fast-mode 'soul coug' and en-GB 'play the movie barbie' failures both PASS on re-run (they were mid-catalog-sync timing artifacts of the interrupted run); no action. (B) BARE-CARRIER COMPETITION (the biggest user-facing class): bare 'Metti {name}' (imperative + name, no noun) is stolen by PlayRadioIntent with station=None ('Metti beatles' 2/2, 'Metti thriller' 1/1) or by AddToQueueIntent with empty slots ('Metti hotel california'); these forms were NEVER pinned samples - they relied on the OLD skill's trainer generalization, and the recreated trainer routes them to whichever intent carries 'metti' samples. 'Suona {artist}' survives (PlayArtistSongs 2/2). Decision needed per the anti-pattern rules: either add explicit noun-less artist/album samples (risk: greedy bare carriers, anti-pattern #11!) or update fixtures to the noun forms and document bare 'metti X' as unsupported. The #11 precedent (bare album carriers REMOVED deliberately) argues for FIXTURES + guidance, not model surgery. (C) MEDIA-INFO SLOT-FILL: 'Che brano e questo'/'Was laeuft gerade' route to MediaInfoIntent correctly but media_info_type stays None (en-GB 'What song is this' fills 'song'); the handler already tolerates the empty slot (generic info answer) - fixture shape, likely class (b); verify the fixture only expects intent, not slot fill. (D) SEARCH-VERB NO_SELECT: 'Trova/Find inception' NO_SELECT deterministically in 2+ locales; the samples are qualified ('Trova il film {query}') and the bare verb+title form was again trainer generalization; per anti-pattern #3 the qualified carriers are the design - fixtures should use the qualified forms or accept NO_SELECT for bare ones. es-US mass failures are (B)+(D) combined. NEXT per the trainer-nondeterminism memory: each proposed model-side fix needs its own 6-10 probe battery; recommend deciding (B) first since it affects the most fixtures.

2026-09-20 triage round 2, the (B)/(D) decision batteries (live, deterministic): (B) RESOLVED as FIXTURES + guidance, evidence: the noun forms restore correct routing - 'Metti la musica dei beatles' -> PlayArtistSongs, 'Metti l'album thriller' -> PlayAlbum (album FILLED), 'Metti la canzone hotel california' -> PlaySong; bare 'metti X' was never a pinned sample (generalization). One surprise: 'Metti i brani dei beatles' -> PlayRandomIntent media_type='i brani' - the noun form collides with the random-media noun carrier; file separately or fold into the fixture batch. (D) RESOLVED as FIXTURES: the EXACT sample shapes route deterministically 3/3 ('Trova un film inception' -> SearchMedia query filled); the failing fixtures used forms that are NOT samples ('Trova il film', 'Find the movie' - note en-US's sample is 'Find a movie', 'the' variant was generalization). CONCLUSION for both classes: the recreated trainer lost the generalizations but keeps exact-sample routing 100%; the fixtures pinned generalized forms. The batch work is now purely mechanical: rewrite the ~65 fixture rows to the exact sample shapes (noun/carrier-qualified) with a comment naming the trainer re-roll, then a full NLU re-run to confirm the residual.

2026-09-20 CORRECTION of the task's own premise (found while extracting the definitive list): the '65 failed / 987 passed' run was the FULL integration suite (test_nlu + test_e2e collected together), so 65 = 53 E2E failures (the invocation-layer wall, already classified under JF-551/595) + ONLY 12 REAL NLU fixture failures: de-DE 'spiele stranger things staffel zwei folge eins'; en-CA 'Play a random movie'; en-GB + en-US 'Play the song hotel california from the eagles'; en-IN 'play stranger things season two episode one'; en-US 'Play imagine by john lennon next'; es-MX 'recuerdame en treinta minutos'; fr-FR 'joue stranger things saison deux episode un'; it-IT 'Riproduci star wars' + 'suona star wars' + 'Continua a guardare dark'; ja-JP 'queen no uta wo sagashite'. The '66->65 unchanged' comparison in rounds 1-2 stands (same mix), but the scale of the fixture work is 12 rows, not 65. The 12 cluster into: (i) season/episode worded addressing in 4 locales (de/en-IN/fr) - the JF-583 episode_position family, likely the same trainer-generalization loss; (ii) 'song by artist' compound (en-GB/en-US hotel california from the eagles) + 'play X next' (en-US imagine) - the JF-345/JF-578 carrier families; (iii) it-IT 'star wars' video-title steals (the PlayNextIntent steal, round-1 class); (iv) es-MX sleep timer word form; (v) ja-JP find-song form; (vi) en-CA 'Play a random movie' (PlayRandom media_type); (vii) it-IT 'Continua a guardare dark' (ContinueWatching). Round 2's conclusion still holds: rewrite fixture rows to exact-sample shapes; the batch is now tractable in one sitting.
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
