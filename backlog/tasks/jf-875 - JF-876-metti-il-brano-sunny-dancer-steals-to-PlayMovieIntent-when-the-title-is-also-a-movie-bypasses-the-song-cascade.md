---
id: JF-875
title: >-
  JF-876: "metti il brano sunny dancer" steals to PlayMovieIntent when the title
  is also a movie (bypasses the song cascade)
status: To Do
assignee: []
created_date: '2026-10-12 21:12'
updated_date: '2026-10-12 21:25'
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
<!-- SECTION:NOTES:END -->
