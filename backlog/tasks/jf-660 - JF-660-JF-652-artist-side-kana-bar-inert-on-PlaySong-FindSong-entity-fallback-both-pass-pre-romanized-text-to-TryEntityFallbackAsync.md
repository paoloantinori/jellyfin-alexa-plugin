---
id: JF-660
title: >-
  JF-660 - JF-652 artist-side kana bar inert on PlaySong/FindSong entity
  fallback: both pass pre-romanized text to TryEntityFallbackAsync
status: To Do
assignee: []
created_date: '2026-09-28 11:25'
updated_date: '2026-09-28 13:55'
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
  - >-
    backlog/tasks/jf-654 -
    JF-654-the-song-side-kana-bar-is-missing-ビートルズ-refuses-the-wrong-artist-but-TrySongFallback-then-auto-plays-a-wrong-song-Bitters-Absolut-for-the-kana-derived-query.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-654 /simplify altitude review (out-of-scope observation, verified against the code the same turn).

THE GAP: CrossMediaFallback.TryEntityFallbackAsync computes its artist-side kana-origin flag from its own slotText param pre-romanization (CrossMediaFallback.cs, the `bool kanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(null, slotText);` line, JF-652/JF-659), and its doc claims "the callers pass raw slot text". But TWO callers hand it ALREADY-ROMANIZED text:
- PlaySongIntentHandler.cs:339 passes `songQuery`, romanized at entry since JF-643 (PlaySongIntentHandler.cs:204).
- FindSongIntentHandler.cs:590 passes `keywords`, the romanized local (FindSongIntentHandler.cs:471).

IsKanaOriginQuery on a romaji string returns false, so the JF-652 artist-side kana bar is INERT on those two paths: a kana-origin song/keywords miss ('ビートルズ' -> 'bitoruzu') falls into the plain cross-media artist gates. With the default JF-363 Confirm mode the live class ('bitoruzu' vs 'Sator' ~60) surfaces as a spurious "did you mean Sator?" OFFER rather than a play; a >= 85 plain-fuzzy artist collision would auto-play. Same wrong-accept family JF-652 killed, surviving through two caller paths it never saw.

THE FIX SHAPE (mirror JF-654's song-side threading): thread a kanaOrigin flag into TryEntityFallbackAsync (default false), computed by the callers on the RAW slot pre-romanization via ArtistSearch.IsKanaOriginQuery (PlaySong and FindSong already hold those raws; PlaySong computes a song-slot kanaOrigin local since JF-654 that can be reused, FindSong's is on the stored raw Keywords). Alternatively hoist a RomanizeWithOrigin composite on KatakanaRomanizer so the capture-then-romanize couplet (now at ~8 sites) enforces its ordering by signature (the JF-654 /simplify pass noted the couplet but skipped it as outside that diff).

VERIFICATION BAR: a kana-origin PlaySong title miss takes the honest song not-found (never an artist offer/play without collision evidence), same for a FindSong keywords miss; the Latin cross-media matrix unchanged.
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
2026-09-28 LIVE EVIDENCE (deployed a7d42b09, simulator, log-corroborated): PlaySong song=ビートルズ -> the JF-654 song bar correctly refuses ('no song or album named bitoruzu'), then the JF-363 cross-media ARTIST SUGGESTION fires offering Sator ('アーティストの Sator さんのことでしょうか?'): the suggestion band accepted the plain-fuzzy Sator match because the entity-fallback path passed the pre-romanized text and the JF-652 artist flag was inert - exactly this task's leak, now observed live. Fix priority note: the suggestion (not-found follow-up) leaks the wrong artist NAME into a yes/no prompt; the yes plays Sator. The same flag threading this task specifies closes it.
<!-- SECTION:NOTES:END -->
