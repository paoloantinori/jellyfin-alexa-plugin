---
id: JF-962
title: >-
  hi-IN film family (systematic) + the two token/carrier decisions (gana x
  BrowseCategory, music-by artist carrier) + the dinner-mood annotation
status: Done
assignee: []
created_date: '2026-11-25 10:56'
updated_date: '2026-11-27 21:53'
labels:
  - nlu
  - carrier-decision
  - hi-IN
  - en-US
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-959 re-verification census (2026-11-25): the hi-IN and en-US carrier decisions. (1) hi-IN films: four Fallback rows are STILL-RED-CONFIRMED and SYSTEMATIC (lab no-selection too): the Devanagari utterance exactly instantiating the template sample reads Fallback, so the template itself lacks trainer weight or the film-carrier family needs anchoring rows; investigate with the JF-954.4 graph (what swallows or neighbours the film family in hi-IN) before touching carriers. (2) hi-IN 'gana shahar ki awaz chalao' (Devanagari): the word is BOTH a PlaySongIntent carrier and a BrowseCategory value (the issue-46 token-ownership class, live-reproduced with browse_category matching); the fix class is vocabulary-side (move or qualify the colliding value) and per the standing carrier rule needs the maintainer's decision with at least one real user. (3) en-US 'give me music by coldplay': no music-by carrier row exists on the artist intent, so PlaySongIntent wins with a hallucinated entity (identical in live AND lab, so not catalog-dependent); adding a 'music by {artist}' carrier to PlayArtistSongs is the fix candidate, again a carrier decision. (4) en-US 'play dinner music': trainer-marginal (winner varies by build; dinner IS in the Mood vocabulary) — annotate the fixture comment rather than chasing it.
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
CLOSED all-four-resolved (2026-11-27): (1) hi-IN films: the systematic-fallback investigation IS the JF-770 arc, now executed to its terminal finding (content-reproducible deterministic burial, template-unreachable, parked on the device round with the workaround video/movie). No separate action remains. (2) gana x BrowseCategory: the collision is GONE - HEAD's hi-IN model carries NO gana value in BrowseCategory (removed in the interim; the JF-963 audit's baseline prune also dropped the stale identities); gana remains a pure carrier word across the song intents with no token-ownership conflict to decide. (3) en-US 'give me music by coldplay': the premise is stale - PlayArtistSongsIntent now carries a 14-row music-by family INCLUDING 'give me music by {musician}'; live-verified 2026-11-27 (run 20261127-225137): the utterance routes PlayArtistSongsIntent with musician=Coldplay (ER-resolved), 3/3 stable readings, 'play music by pink floyd' and the fixture-family rows green alongside. No hallucination reproduces. (4) 'play dinner music': the fixture row now carries the trainer-marginal annotation (winner varies by build; the JF-959 census + the JF-974 lab round's steal-then-resolve history recorded inline) - annotated, not chased, per the task's own disposition. gate-exempt: the only change this round is one fixture COMMENT (zero rows, zero code, zero model bytes); the closure evidence is tool output (carriers, model dumps) plus the recorded live run.
<!-- SECTION:FINAL_SUMMARY:END -->
