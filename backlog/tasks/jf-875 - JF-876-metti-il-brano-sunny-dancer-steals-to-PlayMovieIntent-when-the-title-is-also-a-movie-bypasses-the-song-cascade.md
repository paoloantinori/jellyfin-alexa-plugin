---
id: JF-875
title: >-
  JF-876: "metti il brano sunny dancer" steals to PlayMovieIntent when the title
  is also a movie (bypasses the song cascade)
status: Done
assignee: []
created_date: '2026-10-12 21:12'
updated_date: '2026-10-16 08:18'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual 2 from JF-874's A/B (worker report, 2026-10-12 night): with PlayMovieIntent live, "metti il brano sunny dancer" now selects PlayMovieIntent (stable over three scratch builds), because the movie catalog's ER match outranks the PlaySong carrier on that exact shape; "suona il brano wicked" and "metti il brano la la land" are unaffected. Consequence: for a title that is BOTH a movie and a song word-shape on the "metti il brano X" carrier, the JF-871 song-side cascade is bypassed (the movie launches instead of the song playing). Likely fix directions: carrier weighting (the brano noun should dominate the movie slot's pull), or the PlayMovie handler detecting the brano/song carrier in the raw utterance and declining to the song path, or accepting as a documented trade-off if the household has no movie-titled songs. Verify on the live model after JF-874's deploy (the scratch evidence predicts it); decide fix vs accept with the maintainer's one-liner.
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
LIVE OBSERVATION 2026-10-12 23:2x (post-JF-874 deploy, vendor skill, it-IT): the steal did NOT reproduce on the live build. 'metti il brano sunny dancer' reads PlaySongIntent on the deployed model (catalog MovieTitle wired, template rebuilt, wrapper battery green 10/10). The scratch-build prediction (three builds stable) did not transfer; per the nlu_trainer_nondeterminism discipline, do NOT close this on one reading: re-probe 3x on the next model touch and keep an eye on the JF-876 shape. If it stays PlaySong across two model builds, downgrade to a scratch-only anomaly.

EXECUTED 2026-10-13 (maintainer decision: fix now), branch jf876 commit 1a8e6f06 in worktree /tmp/wt-jf876, NOT pushed, NOT deployed. Handler-side guard as directed: PlayVideoIntentHandler detects a leading song-carrier phrase in the RAW movie-slot fill via MultiValueErDisambiguation.TryDetectSongCarrier (new, shares the ONE StripSongCarrierPhrase table) and delegates the whole ask to PlaySongIntentHandler (typed singleton, required ctor param, registration pinned). Whole-span escape keeps carrier-TITLED movies playable (fill == ER canonical verbatim); the RAW fill rides the song slot so exactly one carrier cut happens. No model/template changes. Both TFMs 5904/5904, Release -warnaserror clean, red-green verified. Residuals filed: JF-879 (sibling catalog slots + PlayVideoIntent title path) and JF-883 (stray review findings on JF-879 merge). Known limit: a steal whose fill carries NO carrier noun (clean canonical fill) is invisible to any handler-side guard; if that shape ever shows live, it is a model-side fix.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED 2026-10-13 (maintainer decision: fix now): the handler-side song-carrier guard, merged to main as 1a8e6f06 (gates recorded in that commit: /simplify two rounds, /code-review high with 7 findings applied/tracked, tests both TFMs 5904/5904, red-green verified); residuals filed as JF-879/JF-883. RE-PROBE CLOSURE 2026-10-16 (the task's own protocol: re-probe 3x on the next model touch): the live model has now read 'metti il brano sunny dancer' -> PlaySongIntent on TWO consecutive builds (2026-10-12 post-JF-874 live build, and 2026-10-16 post-JF-889 rebuild, 3/3 probes today plus the 'suona il brano wicked' control also PlaySongIntent), so the scratch-build steal is DOWNGRADED to a scratch-only trainer anomaly per the nlu_trainer_nondeterminism discipline; the merged guard remains as the belt for any live recurrence of the carrier-carrying shape. Known limit stands (documented in the notes): a steal whose movie-slot fill carries NO carrier noun is invisible to any handler-side guard and would need a model-side fix. gate-exempt: record backfill only, code merged as 1a8e6f06 with its gate record in the commit message; no new code this session.
<!-- SECTION:FINAL_SUMMARY:END -->
