---
id: JF-857
title: >-
  Cross-locale single-member search-tail audit: de-DE's Film-carried 'Suche nach
  dem Film' reads NO_SELECTION, the same weak-loanword-on-single-member-tail
  class as hi-IN (14 locales to map)
status: Done
assignee: []
created_date: '2026-10-10 12:14'
updated_date: '2026-10-10 15:27'
labels:
  - nlu
  - interaction-model
  - follow-up
dependencies: []
references:
  - >-
    backlog/tasks/jf-771 -
    hi-IN-film-carried-short-forms-beyond-dekho-khojo-tail-Fallback-and-chalao-stolen-by-PlaySong-bare-song-chalao.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the JF-771 completion round (code-review findings 1-2, the same-turn rule; the worker's dispatch barred backlog edits).

THE DE-DE DATUM (live, 4x repeats): 'Film Inception anschauen' reads NO_SELECTION on the live de-DE model, and de-DE's own 'Suche nach dem Film {title}' is the SAME single-member Film-carried search-tail shape as hi-IN's फिल्म {title} खोजो (which reads Fallback, trainer-anomalous per JF-770/JF-771). So the weak-loanword-carrier-on-a-single-member-tail failure class is NOT hi-IN-specific: it plausibly affects every locale whose movie-carrier loanword rides a one-sample search tail.

THE AUDIT: for each of the other 14 non-it locales, enumerate the movie/video search-and-play tails (the locale's equivalent of the खोजो/देखो/चलाओ family) and check which are single-member; live-probe one carrier per single-member tail (profile-nlu, 4x, selectedIntent-first, raw slot values, live model first verified sample-equal to committed - the JF-771 method). Output: the map of weak tails per locale; for each, either the JF-771 fix shape (add the STRONG carrier variant of the same tail, never the weak-loanword one) or a trainer-anomaly record if even the strong carrier fails. Coordinate with the JF-855 mirror-directive question: if many locales need the same family-strengthening, the generator-level mechanism may beat 14 template edits.

SCOPE GUARDS: probe-first only after the hi-IN rebuild verifies (the JF-771 post-deploy matrix is the running proof of the fix shape); do not touch the hi-IN surfaces (JF-771 owns them until its matrix closes); the trainer-anomaly dispositions land in the affected locales' records, not new task files per locale.
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
POST-DEPLOY MATRIX OUTCOME (2026-10-10 18:20, orchestrator; deploy b5801c88 + de-DE rebuild SUCCEEDED): 10/10 legs exactly as predicted. LEG1-4 (the fix bar): 'Suche nach dem Video Inception' and 'Video Inception anschauen/schauen/zeigen' ALL route PlayVideoIntent 4/4 - the strong-carrier crossing fix works LIVE. LEG5-8 (guards): the exact Film-carried 'Suche nach dem Film Inception' still routes 4/4 (no regression), the native control 'Ich will Inception anschauen' routes, both SearchMedia neighbors ('Suche nach einem Video/Film') hold SearchMediaIntent 4/4. LEG9-10 (record-only): the Film-carried crossings 'Film Inception anschauen/schauen' stay NO_SELECTION 4/4 - the trainer-anomaly disposition confirmed (the same class as hi-IN film, do not template-patch per the two-failed-fixes rule). The 4 red-until-deploy fixture pins are live-green by construction. TASK CLOSED on the full bar; the 12-locale (a)-class wave remains the maintainer's JF-855 decision, recorded above.
<!-- SECTION:NOTES:END -->
