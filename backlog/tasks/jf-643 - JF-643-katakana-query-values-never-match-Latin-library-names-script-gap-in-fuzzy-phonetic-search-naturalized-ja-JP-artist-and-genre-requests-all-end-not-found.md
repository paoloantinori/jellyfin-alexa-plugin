---
id: JF-643
title: >-
  JF-643 - katakana query values never match Latin library names (script gap in
  fuzzy/phonetic search): naturalized ja-JP artist and genre requests all end
  not-found
status: To Do
assignee: []
created_date: '2026-09-27 06:38'
labels:
  - search
  - i18n
  - ja-JP
  - device-found
dependencies:
  - JF-642
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 from the JF-642 investigation (same-turn rule; the mechanism split is documented there).

PROBLEM (simulator-verified on minix, ja-JP locale): katakana query values cannot match Latin library names on ANY search path, because the fuzzy/phonetic layers (FuzzyStrings + Double Metaphone) are Latin-script algorithms:
- PlayArtistSongsIntent musician='クイーン' (Queen) -> artist not-found.
- PlayByGenreIntent genre='ジャズ' (Jazz) -> NotFoundGenre (raw katakana vs the library's Latin 'Jazz').
- The cross-media artist fallback (TryEntityFallbackAsync) fires on the genre steal but fails the same way.
Latin values on the same paths work ('queen', 'Jazz' both resolve/play), so this is purely the script gap. Japanese ASR transcribes foreign artist/genre names as katakana, so the naturalized ja voice hits this on every foreign-name request. The same class will affect any non-Latin-script locale value set (notably ar-SA, hi-IN native script values) - check those while in here.

WHERE: the search entry points that receive raw slot text. ArtistSearch (Alexa/Util/ArtistSearch.cs) + SongNgramIndexService/KeywordMatcher for titles + the genre query in PlayByGenreIntentHandler (Genres array) + CrossMediaFallback.TryEntityFallbackAsync. A query-side normalization (katakana -> romaji transliteration, deterministic syllabary mapping; long-vowel ー, small tsu っ, geminate handling, ノ generative-phonetics rules note JF-379 already owns velar-stop rules for ja) applied BEFORE fuzzy/phonetic scoring would bridge all paths at once. Do NOT transliterate library-side values; the gap is query-side.

VERIFICATION BAR: simulator probes under ja-JP: musician='クイーン' plays Queen (or the expected disambiguation), genre='ジャズ' plays jazz. Unit tests pin the transliterator (katakana string -> expected romaji) and the search-path integration (query katakana, candidate Latin -> match above threshold). No regression in Latin/Latin matching.

OUT OF SCOPE: the routing-layer genre steal (JF-642 owns it; this task is its companion - fixing routing without this still leaves katakana artist voice at not-found, and vice versa).

RELATION: depends on nothing technically; JF-642 dependency recorded only for scheduling coherence (same locale, same evidence base). Both must land for naturalized ja artist voice to work end-to-end.
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
