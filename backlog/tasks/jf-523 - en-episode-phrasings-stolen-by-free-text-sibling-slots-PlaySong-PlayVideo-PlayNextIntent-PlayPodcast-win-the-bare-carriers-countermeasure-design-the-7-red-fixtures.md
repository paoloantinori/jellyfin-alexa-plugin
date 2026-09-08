---
id: JF-523
title: >-
  en-* episode phrasings stolen by free-text sibling slots
  (PlaySong/PlayVideo/PlayNextIntent/PlayPodcast win the bare carriers);
  countermeasure design + the 7 red fixtures
status: To Do
assignee: []
created_date: '2026-09-08 05:53'
labels:
  - nlu
  - interaction-model
  - localization
  - routing-theft
dependencies: []
references:
  - JF-324
  - JF-470
  - JF-508
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live NLU evidence (2026-09-08, bare profile-nlu, deployed model 33dfacd5): on en-* locales the natural episode phrasings are stolen wholesale by free-text slots of sibling intents, while the it-IT family serves correctly (the JF-324 PART 1 confirm passed on it-IT; e2e 10/10 with invocation context). Probes:

- 'Play stranger things season two episode one' -> PlaySongIntent, song='stranger things season 2 episode 1' (all of en-US/AU/CA/GB/IN; the 5 red tests)
- 'play season two episode one of stranger things' -> PlaySongIntent, song='2 episode 1 of stranger things' (tail form ALSO stolen on en-US; it-IT tail fills fine)
- 'play the next episode of stranger things' -> PlayNextIntent, song='episode of stranger things' (the queue-next steals the carrier)
- 'watch the latest episode of stranger things' -> PlayVideoIntent, title='the latest episode of stranger things'
- 'play the latest episode of stranger things' -> PlayPodcastIntent, podcast_name='stranger things' (the exact competition JF-324 PART 1 flagged; en-US verdict: podcast wins; it-IT twin green)
- 'Watch season one episode five of game of thrones' -> PlayVideoIntent, title='season 1 episode 5 of game of thrones'
- GREEN shape: 'ask jellyfin to play season one episode five of game of thrones' -> PlayEpisodeIntent with episode_number='5', season_number='1', series_name='game of thrones' (the invocation-prefixed one-shot escapes the competition)

Also from the same session (it-IT, already fixed in fixtures, recorded here for the pattern): the prefix-series shape '{series} stagione N episodio M' stopped filling series_name (7/7 probes; tail shape fills), and bare 'Continua a guardare {series}' routes to ContinueWatchingIntent while the invocation-context e2e keeps PlayNextEpisodeIntent (JF-298 divergence, both pinned in their own contexts).

The 7 red en-* tests are tracked here (rule: fix or track): they pin genuinely-broken user-facing routing (a user saying 'play stranger things season two episode one' gets a song search), so the countermeasure decision matters more than the fixture bookkeeping.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Full en-* probe matrix completed (12 active locales x the 4 phrasing families), winner + slot captures recorded per cell
- [ ] #2 Countermeasure design evaluated against anti-pattern #3/#11 risks: candidate levers = why the free-text slots win despite the exact qualifying samples existing (sample-count asymmetry 38 vs few, slot-type greed), carrier-noun additions, and/or accepting the theft and pinning fixtures to the prefixed one-shot forms (the only currently-green shape)
- [ ] #3 Fixtures updated to the chosen outcome with probe evidence; the 7 red en-* tests resolved (green or expectation-pinned)
- [ ] #4 Model changes (if any) regenerated + validated + deployed via the rebuild endpoint + live re-verified
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
<!-- AC:END -->

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
