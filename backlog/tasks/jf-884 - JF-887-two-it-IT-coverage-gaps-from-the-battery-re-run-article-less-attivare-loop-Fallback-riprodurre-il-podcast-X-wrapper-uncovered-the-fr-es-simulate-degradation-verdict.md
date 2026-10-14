---
id: JF-884
title: >-
  JF-887: two it-IT coverage gaps from the battery re-run (article-less
  'attivare loop' -> Fallback; 'riprodurre il podcast X' wrapper uncovered) +
  the fr/es simulate-degradation verdict
status: Done
assignee: []
created_date: '2026-10-14 11:17'
updated_date: '2026-10-14 12:18'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two real NLU coverage gaps found by the 2026-10-14 battery re-run (after the false-outage fix; both verified failing on BOTH engines, profile-nlu 3/3 AND simulate, so they are not the JF-298 divergence class):
1. 'chiedi a mia collezione di attivare loop' (it-IT): expected LoopAllOnIntent; profile-nlu reads AMAZON.FallbackIntent 3/3, simulate picked LoopAllOffIntent. The bare-infinitive 'attivare loop' shape (no article, no 'il') has no sample row; 'attivare il mescolamento' (with article) works, so the gap is the article-less loop-on twin. Model fix: add the bare infinitive rows for the loop-on family (probe-first for LoopAllOff steals: 'disattivare loop' must keep its family).
2. 'chiedi a mia collezione di riprodurre il podcast generazione' (it-IT): expected PlayPodcastIntent; profile-nlu Fallback 3/3, simulate AddSongToPlaylistIntent. The podcast wrapper tail 'riprodurre il podcast X' is not covered (check the PlayPodcastIntent sample family for the infinitive carrier with the noun; the JF-844 discipline: probe the Di-form too).
Both are template edits + regen it-IT + probe battery + fixtures. Note the deploy path: DLL (embedded template) + rebuild endpoint.
SEPARATE VERDICT recorded here for the e2e residue: the three smoke failures (es-ES, es-MX 'oye los pink floyd'; fr-FR 'lis les chansons de pink floyd') are NOT ours: the saved models resolve the phrases correctly (profile-nlu: PlayArtistSongsIntent) while simulate-skill resolves NOTHING in fr-FR/es-ES (every phrase, opens included) with it-IT healthy - Amazon's simulate NLU runtime for those locales is degraded (the recurring per-locale class, total phase). The it-IT pink floyd full-chain e2e is GREEN after JF-884+885. The smoke rows go green when Amazon's fr/es simulate recovers; no plugin action.
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
EXECUTED on branch jf887 (d71d3522), merged and pushed (051113e6, with the two out-of-scope fixture fixes folded in: the JF-872 pausa validator arm and the JF-438 duplicate pin dedupe), DEPLOYED to minix (DLL + it-IT rebuild SUCCEEDED) and LIVE-VERIFIED: 'attivare loop' -> LoopAllOnIntent, 'di attivare loop' -> LoopAllOnIntent, 'attivare ripetizione' -> LoopAllOnIntent (the previously unknown misroute fixed too); steal controls green ('disattivare loop' -> LoopAllOffIntent, 'attivare il mescolamento' -> ShuffleAllOnIntent, the OFF wrapper -> LoopAllOffIntent); the podcast shape confirmed live ('riprodurre il podcast generazione' -> PlayPodcastIntent). GAP 2 dissolved under probing: the JF-551 rows were already live and correct; the task's 'Fallback 3/3' was the prefixed-phrase artifact (profile-nlu has no invocation layer). 10 fixture rows (8 model rows green post-deploy + the wrapper red-until-deploy twin + the podcast bare twin); battery ON row is the red-until-deploy pin with the OFF-wrapper steal control added; mirrors regenerated. Gates: worker /simplify + code-review high (5 applied, 1 pre-existing surfaced and landed via the fixture fixes this turn). Validator PASS at the 275 baseline. The fr/es simulate degradation verdict stays recorded in the task body (Amazon-side, no plugin action).
<!-- SECTION:FINAL_SUMMARY:END -->
