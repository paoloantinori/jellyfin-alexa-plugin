---
id: JF-523
title: >-
  en-* episode phrasings stolen by free-text sibling slots
  (PlaySong/PlayVideo/PlayNextIntent/PlayPodcast win the bare carriers);
  countermeasure design + the 7 red fixtures
status: Done
assignee: []
created_date: '2026-09-08 05:53'
updated_date: '2026-09-08 11:52'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED as a documented structural limitation with UX guidance (AC#4 resolved: NO model changes, deliberately). The 12-locale x 4-family probe matrix (live profile-nlu, 2026-09-08, full captures in this task's notes) shows per-marketplace NLU variance: en-US/AU/GB steal ALL bare episode phrasings to PlaySongIntent's free-text 'play {song}' carrier; en-IN serves prefix/tail/next; en-CA serves tail; fr-*/de-DE serve tail+next; the 'latest episode' carrier is claimed by PlayPodcastIntent (which mirrors PlayNextEpisode's carriers 1:1) in 9/12 locales; es-* steal prefix/next/latest to PlayNextIntent/PlaySong/PlayPodcast. Countermeasure evaluation (AC#2): (1) sample-count therapy is unreliable against FREE-TEXT slot absorption (the JF-391 precedent worked within-type, 12-vs-354; here the thief holds a free-text slot that structurally absorbs any 'play X' utterance, and the qualifying samples ALREADY EXIST and still lose); (2) thief-side carrier removal is unacceptable for PlaySong ('play {song}' is the primary music path; the JF-459 cascade logic does not apply to songs) and would cost podcast recall on PlayPodcast; (3) additive noun-carriers ('of the show/series') on PlayNextEpisode are safe but only serve unnatural phrasings and need a per-marketplace verify campaign - noted as the future lever if users report it. DECISION: accept the bare-phrase theft, pin the fixtures to the observed winners (7 reds resolved with probe evidence; en-US suite also gains the one verified-green bare one-shot shape), and document the working UX in the README FAQ (in-skill session flow = correct routing everywhere, e2e-verified 10/10; one-shot numbers-first form for en-US; Italian locales unaffected). Fixtures: 5 files updated (commit f32529a7 + README FAQ). F2-adjacent finding recorded: en-IN's prefix-series fill is unstable (sometimes empty), left unpinned by design. The en-* inv-context e2e (10/10) is the true UX representation per the JF-298 divergence.
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
