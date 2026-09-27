---
id: JF-652
title: >-
  JF-652 - kana-derived queries are wrongly accepted by Latin-calibrated
  thresholds: クイーン plays Keane (silent 91-tie) and ビートルズ plays Sator
  (plain-fuzzy false positive); the JF-643 completion blocker
status: To Do
assignee: []
created_date: '2026-09-27 09:07'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
  - device-found
dependencies: []
references:
  - >-
    backlog/tasks/jf-643 -
    JF-643-katakana-query-values-never-match-Latin-library-names-script-gap-in-fuzzy-phonetic-search-naturalized-ja-JP-artist-and-genre-requests-all-end-not-found.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-643 live verification battery (deployed a39eb2a8, minix simulator + debug logs; this is the completion blocker for JF-643's artist-path rows).

LIVE EVIDENCE (ja-JP, simulator, log-corroborated):
1. musician=クイーン -> romanizes to 'kuin'; ArtistSearch tier-4 FuzzyMatchPhonetic best=Keane matched=True, results=1, auto-played Keane ('A Bad Dream'). Queen IS in the library and DM-collides with Keane at KN; both should floor at 91 (length band OK) and TIE, but FindBestMatch returns a single best (tie -> first in iteration order) and the >=85 auto-play band fires on it. A confident wrong artist, announced by name on the cross-media path ('アーティストKeaneを見つけました').
2. musician=ビートルズ -> romanizes to 'bitoruzu'; tier-4 fuzzy accepted 'Sator' (an unrelated Italian artist in the library) at threshold 60. No DM collision; a plain-fuzzy false positive.
3. genre=ジャズ -> PLAYS jazz (the vocabulary-resolution tier is exact-match on the library's own tags: no wrong-accept class). genre=Jazz Latin control unchanged. The GENRE rows of JF-643's bar PASSED.

ROOT CAUSE: JF-643's romanizer admits a query class (kana-derived romaji) whose score distribution the acceptance machinery was never calibrated for. The thresholds and the single-best auto-play decision encode Latin-ASR-drift assumptions (tight length bands, rare exact code ties between two real artists). Three failure shapes, all live-observed: (a) exact-tie between two floor-91 artists resolves silently to one (should disambiguate: the JF-420 margin rule says margin<=20 -> prompt, but the multi-candidate margin logic lives at the handler's containment gate, not the phonetic single-best path); (b) plain-fuzzy accepts at 60 for syllable-heavy romaji (bitoruzu is 8 chars against a 5-char name, PartialRatio inflates); (c) no kana-awareness anywhere in the acceptance chain.

FIX DIRECTION (needs its own reviewed change; the JF-377 philosophy applies: a prompt or an honest not-found beats a confident wrong artist):
- Kana-origin queries (the slot contained kana pre-romanization) get a STRICTER auto-play bar at the decision points (e.g. require the phonetic floor, i.e. a real DM collision, for auto-play; plain-fuzzy-only matches at 60 downgraded to a yes/no confirm or not-found).
- Exact ties or margins <= epsilon between multiple above-bar candidates at the phonetic path fire DisambiguateMultipleArtists (move/duplicate the margin logic to where the single-best is chosen, or return the runner-up from FuzzyMatchPhonetic).
- Thread the kana-origin flag from the entry sites (the raw slot is available at every JF-643 call site) into the acceptance decision; do NOT change thresholds for Latin queries (byte-identical Latin behavior is JF-643's pinned contract).

VERIFICATION BAR (the failed rows, re-run): musician=クイーン -> Queen plays OR the yes/no artist disambiguation prompt naming Queen among candidates; musician=ビートルズ -> Beatles plays OR not-found/confirm (NEVER Sator); genre=ジャズ and genre=Jazz unchanged (still playing). Plus the Latin regression matrix (artist e2e rows) unchanged.

RELATION: blocks JF-643's completion (its artist-path bar rows); JF-642's ROUTING rows are unaffected (routing is NLU-side; this is handler-side acceptance). JF-645's residual list is separate (coverage); this is precision.
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
