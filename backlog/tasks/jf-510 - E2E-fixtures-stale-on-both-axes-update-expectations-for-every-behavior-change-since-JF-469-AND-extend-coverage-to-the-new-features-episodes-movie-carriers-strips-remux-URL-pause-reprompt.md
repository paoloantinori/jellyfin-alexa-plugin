---
id: JF-510
title: >-
  E2E fixtures stale on both axes: update expectations for every behavior change
  since JF-469 AND extend coverage to the new features (episodes, movie
  carriers, strips, remux URL, pause reprompt)
status: To Do
assignee: []
created_date: '2026-09-06 19:10'
labels:
  - e2e
  - tests
  - debt
dependencies: []
references:
  - JF-469 (last fixture touch)
  - JF-487
  - JF-488
  - JF-489
  - JF-490
  - JF-492
  - JF-493
  - JF-498
  - JF-504
  - JF-505
  - JF-509
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 direction: the E2E fixtures (tests/integration/fixtures/e2e_it-IT.yaml, e2e_en-US.yaml, e2e_reliability_it-IT.yaml) are stale at JF-469 (2026-09-03) on BOTH axes: they neither track the behavior changes landed since NOR cover the new features at all. The suite is runnable autonomously (./scripts/run_e2e_tests.sh with ASK_SKILL_ID + JELLYFIN_URL/API_KEY/USER; orchestrator-proven 2026-09-06) so this debt is pure fixture work, no tooling needed.

AXIS 1 - UPDATE EXISTING EXPECTATIONS for landed behavior changes (verify each against the live suite, then update the fixture):
- JF-488: pause with PauseKeepsSession (now default ON) speaks 'Pausa.' + reprompt with shouldEndSession=false; the 'pausa' fixture's loose response_type:any should be tightened to pin the reprompt presence.
- JF-487: FindSong single-candidate auto-play ('Riproduco X di Y', no 'Quale?'), singular count grammar, ArtistName from item metadata ('di Soul Coughing', never 'di Unknown'); welcome SSML separators.
- JF-492/JF-509: the calling-word/musician-strip and media-noun-strip recoveries change not-found shapes ('cerca un album chiamato X' now plays; 'film ada' heals to 'ada').
- JF-498: video items with incompatible audio route to the HLS remux URL (/alexaskill/api/video-audio/episode/{id}/stream.m3u8) in the stream directive instead of /Videos/static; movies (compatible) keep static.
- JF-493: PlayNextEpisode ('il prossimo/ultimo episodio di {series}', 'continua a guardare {series}') with the catalog-backed series fill; PlayEpisode without explicit numbers falls to NextUp.
- JF-504: movie-by-title carriers ('il film {title}') route; the swallowed-noun shape heals.
- JF-505: the VideoApp gate is fail-open on absent interface maps (the simulator context) so video fixtures keep working, but a screenless-context pin belongs in the unit suite only - do NOT try to e2e it.

AXIS 2 - EXTEND with new-feature coverage (all absent today):
- Next/latest episode plays (JF-324/JF-493): 'metti il prossimo episodio di <series>', 'l'ultimo episodio di', 'continua a guardare', and the explicit 'la stagione N episodio M di' numbers-first shape.
- Movie by title (JF-504): 'voglio guardare il film <title>' and the JF-509 swallowed-carrier shape (assert the play directive or the VideoApp source URL).
- Album calling-word family (JF-489/JF-492): 'un album chiamato <title>' and 'cerca un album chiamato <title>'.
- che-si-chiama carriers (JF-490 + follow-up): 'un album che si chiama <title>' and the verb-ful form.
- Series catalog fill (JF-493): a real-library series name resolves (ER_SUCCESS_MATCH shape is NLU-level; e2e asserts the launched item name).
- The JF-498 remux source-URL assertion on one h264+eac3 episode vs the static URL on one compatible movie.

CONSTRAINTS: e2e en-US competes with built-in skills (known flakiness) - keep new coverage on it-IT where possible; respect SMAPI_DELAY=1.5 (suite wall-clock scales with test count: keep additions ~20-25 tests, not double the suite); every new fixture must assert something specific (intent + the NEW behavior marker: reprompt presence, m3u8 source, played item name), never response_type:any, or the stale-fixture failure mode returns. Workflow: run the live suite FIRST, triage each failure into 'fixture stale' vs 'real regression' (file real ones), then update+extend in one pass, re-run to green, commit with gates.
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
