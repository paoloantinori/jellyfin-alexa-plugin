---
id: JF-654
title: >-
  JF-654 - the song-side kana bar is missing: ビートルズ refuses the wrong artist but
  TrySongFallback then auto-plays a wrong song (Bitters & Absolut) for the
  kana-derived query
status: To Do
assignee: []
created_date: '2026-09-27 12:22'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
  - device-found
dependencies: []
references:
  - >-
    backlog/tasks/jf-652 -
    JF-652-kana-derived-queries-are-wrongly-accepted-by-Latin-calibrated-thresholds-クイーン-plays-Keane-silent-91-tie-and-ビートルズ-plays-Sator-plain-fuzzy-false-positive-the-JF-643-completion-blocker.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-652 live battery (deployed a9c57451, minix simulator).

THE FINDING: musician=ビートルズ (Beatles) under ja-JP now refuses the wrong ARTIST (the JF-652 kana bar works: 'bitoruzu' has no DM collision with any artist, so Sator-style artist false-accepts are dead) but the JF-439 TrySongFallback then PLAYS A WRONG SONG: 'bitoruzu' fuzzy-matched the song 'Bitters & Absolut' and auto-played it with the found-song announcement. The song path was deliberately left out of JF-652's scope (the dispatch judged the JF-508/JF-526 full-keyword coverage gate sufficient); the live result shows it is not, for kana-derived queries.

THE GAP: the song-side acceptance (TrySongFallback + the KeywordMatcher coverage gate) has no kana-awareness: a romaji syllable soup can clear keyword-coverage bars against short English titles (bitoruzu tokens vs Bitters/Absolut tokens). The wrong-accept class is the same one JF-652 killed on the artist path, one layer over.

THE WORK (mirror JF-652's shape at the song decision points): for kana-origin queries (the slot contained kana pre-romanization), require a real Double Metaphone collision (or an equivalently strict bar: exact/very-high plain score, e.g. >= 95, since the JF-652 review established plain 91-99 does not prove collision) at the TrySongFallback acceptance and any song auto-play decision point fed by a kana-origin query; reuse ArtistSearch.PassesKanaOriginAcceptance / the codes-carried overloads where the shapes fit.

VERIFICATION BAR: musician=ビートルズ -> honest not-found (never a wrong song); title-keyword katakana queries (FindSong path) get the same bar; the Latin song matrix unchanged.
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
