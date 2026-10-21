---
id: JF-917
title: >-
  en-US: "put on music by X" steals to PlaySongIntent with a phantom song fill
  (sample competition with "put on {song} by {musician}"; the artist row exists)
status: Done
assignee: []
created_date: '2026-10-20 10:55'
updated_date: '2026-10-21 06:13'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual from the JF-914 en/de/fr audit (2026-10-20, probe-proven stable 2/2): the en-US one-shot "ask jellyfin player to put on music by pink floyd" routes PlaySongIntent with a PHANTOM song fill ("\"Put Some Music On\" by Boyz II Men"; the bare payload steals too, different phantom). NOT a wrapper-tail gap: the exact PlayArtistSongs row "put on music by {musician}" exists in templates/en-US.yaml (the {artist_media_verb} x {artist_media_noun} x {artist_media_prep} product); the steal is PlaySong's competing committed exact-pattern sample "put on {song} by {musician}" absorbing song="music" (the documented phantom-MusicRecording class, the JF-399 template comment). The it-IT regression class does NOT apply here (en's wrapper payload is the bare imperative and it routes; "play music by X" and "listen to music by X" are GREEN full-pipeline). FIX SHAPE: a sample-competition problem between two committed en samples; options to weigh probe-first: (a) qualify the PlaySong sample (e.g. "put on the song {song} by {musician}") - a committed-sample EDIT, the JF-541 statistics discipline applies and device verification is mandatory (the sample may be load-bearing for real song asks "put on X by Y"); (b) add a higher-signal PlayArtistSongs anchor. Whatever lands needs the en battery + fixtures + a model deploy. Verification: "ask jellyfin player to put on music by pink floyd" routes PlayArtistSongsIntent on simulate AND "put on X by Y" song asks still route PlaySong (the reverse guard must be probed BEFORE choosing a shape).
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
SHIPPED AND LIVE-VERIFIED 2026-10-21. Merged across 442165a1 + 606fd605 + d5e5add5 (CI+CodeQL green on every push), deployed (final md5 f85bb5a2, AlexaSkill_1.0.1.0, config survived). THE FIX (handler-side, the model deliberately untouched per the reverse-guard evidence): the JF-715 generic-word bypass now also fires on the OBSERVED whole-phrase wrapper residues; TWO live captures exist for the same ask (the NLU's fill boundary varies per call): "on music" (simulate 2026-10-20) and "put on music" (the e2e harness delivery, log corr 0109f99f), both with the AlexaEntities phantom resolution on the song slot; the table is whole-phrase only, never derived (the round-3 review correctly rejected my strippable-particle first cut: generic "on" stripping would swallow the "On Track" title class into the artist bypass; the boundary is pinned by the UnobservedResidueFill test and four membership-table rows). VERIFICATION: live simulate answers "now playing: Comfortably Numb" (AudioPlayer.Play, title Comfortably Numb by Pink Floyd) for "ask jellyfin player to put on music by pink floyd" (4/4 draws incl. the exact harness sequence with the failed reset); the unit Theory covers both fills; the e2e row (expected_intent PlaySongIntent + the artist's AudioPlayer.Play + /Audio/) is deployed-against and left to the next suite run (the session's tool classifier outage blocked the immediate pytest re-run; the row's premise is the exact shape the live simulate proved). 6113 tests per TFM (baseline 6106 + 7 new). The stale pre-fix behavior (the honest not-found "couldn't find any songs with the name put/on music") is preserved for every unobserved fill shape per the JF-697 doctrine.
<!-- SECTION:FINAL_SUMMARY:END -->
