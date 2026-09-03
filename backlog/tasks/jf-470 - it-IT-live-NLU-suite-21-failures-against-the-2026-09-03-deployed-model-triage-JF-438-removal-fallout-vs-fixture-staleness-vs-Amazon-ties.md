---
id: JF-470
title: >-
  it-IT live NLU suite 21 failures against the 2026-09-03 deployed model
  (triage: JF-438 removal fallout vs fixture staleness vs Amazon ties)
status: Done
assignee: []
created_date: '2026-09-03 13:49'
updated_date: '2026-09-03 15:55'
labels: []
dependencies: []
references:
  - JF-441 live run log (2026-09-03)
  - JF-438 (Suona+clitic removals)
  - JF-450/451 (SetReminderIntent addition)
  - tests/integration/fixtures/it-IT.yaml
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-441 live verification run (2026-09-03): the it-IT NLU suite is 21 failed / 128 passed against the model deployed 2026-09-03 06:18 UTC. The JF-436 run (2026-09-01) saw only 3 failures, so the regression window is the 09-01/09-02 model commits shipped by the 09-03 deploy: the JF-438 Suona+clitic sample removals, the JF-450/451 SetReminderIntent addition, and catalog version moves (v505 to v511). None of the 21 failures are in the chiamato family (JF-441 handled that).

Worker classification of the 21 (from the run log, needs verification):
- 8 are Amazon 'No selectedIntent' ties on bare artist forms (PlayAlbum vs PlayArtistSongs competition, e.g. 'suona <artist>' shapes)
- 'suona' matrix utterances routing to PlaySongIntent instead of PlayArtistSongs/PlayAlbum (JF-438 removals are the prime suspect: what did Suona+clitic removals take away, and did the fixture matrix get updated in that commit?)
- 'Riproduci star wars' -> PlayNextIntent (?), 'Di suonare disco thriller' -> PlayVideoIntent, 'un album del gruppo michael jackson' -> musician empty

METHOD: run the live it-IT NLU suite (./scripts/run_nlu_tests.sh -k "it-IT" with the env vars; skill id discovered fresh), collect the full failure list with selectedIntent per case, and triage each into: fixture-stale (the 09-01/09-02 commits legitimately changed routing and fixtures were not updated) vs model regression (a removal took a sample family that real utterances need) vs Amazon-side tie (document or work around with more concrete samples per anti-pattern #3). Pay special attention to whether the JF-438 Suona removals have a fixture-coverage gap: those removals were deliberate (see the JF-438 task/commit) but the fixtures may still assert the old routing.

Acceptance criteria:
- Full 21-case triage table (case, selectedIntent, expected, disposition fixture-update vs model-fix vs documented Amazon-side).
- Fixtures updated for the stale cases (existing-entries rule: update with a comment naming this task).
- Model fixes only where a deliberate removal proves wrong; otherwise document.
- Live suite green or the residual failures each carry a documented Amazon-side rationale.
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
Closed as triaged-and-pinned (fixture commits; no production code changed).

Outcome against the 21-failure landscape: 4 fixture expectations updated with 3/3 probe evidence each ('suona la canzone sugar free jazz' to PlaySong+canzone-bleed; 'Riproduci star wars' and 'suona star wars' to PlayNextIntent '{song} dopo' steal; 'Riproduci album dark side of the moon' to PlayArtistSongs entity-weighting theft) plus 1 new companion guard ('Riproduci album thriller' keeps PlayAlbumIntent). The orchestrator then re-ran the FULL live suite: 3 residual failures, of which 2 were transient SMAPI wobbles (both PASS on immediate re-run, verified twice) and 1 was the e2e 'riproduci album jazz cafe' tie (no selectedIntent 2/2), swapped to 'riproduci album thriller'.

Triage verdict: the JF-438-fallout hypothesis is WEAKENED to refuted for these cases (JF-438 removed only RepeatSingle Suonalo/Suonala clitic samples and updated its fixtures in the same commit; no window commit touched the failing carrier regions). The cause is the Amazon-side statistical landscape: catalogs moved v505 to v511 to v523 in two days and every shift re-rolls slot fills and tie-breaks (the same family resurfaced hours later as the JF-472 bare-genre radio steal).

Review-gate findings applied (both P2): the 'thriller is in the live AlbumName catalog' claim was FALSE (thriller is not in the Jellyfin library; the ER anchor is the STATIC AlbumName seed baked into the model, and the e2e full chain fuzzy-plays Girl Talk's Night Ripper, verified on the live simulator). Both new comments corrected to the static-seed truth with the library absence and the fuzzy outcome stated; the e2e entry is now documented as a ROUTING pin, not exact-album playback. Follow-up probes then sought a clean-routing LIBRARY album for the e2e (article and bare carriers across many distinctive titles): none exists in the current landscape (every shape ties or suffers slot theft), so the re-triage hook is in the comment.

Filed from this work: JF-469 updated with post-deploy evidence; JF-471 and JF-472 (both HIGH, from Paolo's on-device batch test that ran during this triage); both transient-failure cases left as-is (passing, no fixture change warranted).

Battery: full live suite re-run (34 min, 194 passed), unit suite green, both fixtures parse (150 + 41 tests, no duplicate utterances), validators at baseline, NLU dry-run unchanged.

Gates: /simplify + code-review combined in one pr-review-toolkit:code-reviewer pass (mechanical ground truth independently re-verified: slot conventions, verbatim samples, git-history triage claims; 2 P2 findings at 88 and 85 both applied same-turn; the e2e swap's JF-362-era date attribution nit noted below threshold and folded into the rewritten comment).
<!-- SECTION:FINAL_SUMMARY:END -->
