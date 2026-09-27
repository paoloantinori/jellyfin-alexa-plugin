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

## Implementation Notes (2026-09-27)

FLAG PLUMB (score-detection shape, per the design's "your call" clause): `FuzzyMatcher.IsPhoneticFloorScore(int)` (internal, `score >= PhoneticFloorScore` i.e. >= 91) is the detectable floor exposure. Detection reads the score AT the acceptance decision points, which already hold the winner's score, so no flag plumb runs through the search chains and `ArtistSearch.SearchAsync` needed NO kanaOrigin parameter (deliberately not added; the design's optional-param option was for a flag-threaded plumb, which the score-detection choice makes dead weight). The kana-origin flag itself (`KatakanaRomanizer.ContainsKana` on the PRE-romanization raw slot) is captured at the three entry points that own acceptance decisions:
1. `PlayArtistSongsIntentHandler.HandleAsync` entry (before its Romanize line).
2. `CrossMediaFallback.TryEntityFallbackAsync` entry (computed internally, before the shared Romanize).
3. `PlayAlbumIntentHandler` entry (feeds the JF-471 `PassesArtistMatchAcceptance` gate via a new optional `bool kanaOrigin = false` parameter; acceptance tail shared in `CrossMediaFallback.PassesKanaAware`).

GATES (compose with JF-377/JF-420, never replace; Latin behavior byte-identical, every branch behind `if (kanaOrigin ...)`):
- Handler: one end-of-chain gate (`ApplyKanaOriginAcceptance`) placed after the JF-382 containment end-gate, where every collapse path (tier single-best, Fast-mode best pick, HandleFuzzyMiss auto-accept) has already reduced to one pick. Accept only when score >= user threshold AND `IsPhoneticFloorScore`; otherwise the honest not-found (the JF-439 TrySongFallback is still attempted first, preserving the existing artists.Count==0 path). Near-tie (runner-up from the full in-memory pool, both >= threshold, margin <= `ArtistSearch.KanaOriginTieMargin` = 5) fires the existing `DisambiguateMultipleArtists` yes/no ask (JF-420.2 shape, winner listed first).
- Cross-media: same bar at the strict acceptance point; a non-floor strict score, the JF-363 sub-strict band, and the JF-440 word-coverage valve all return null (honest miss, never a confirm prompt for the fuzzy tier). Near-tie fires the same `DisambiguateMultipleArtists` ask built in `ResolveKanaOriginTie` (pool from the pinned index, library-scoped so an excluded-library name is never spoken, JF-457).
- Shared const `ArtistSearch.KanaOriginTieMargin` (single definition, both decision points).

LEFT ALONE (and why): the genre vocabulary tier (exact-match on library tags, no wrong-accept class, live-verified); the song/album/video/book/podcast fuzzy paths (their acceptance already carries the JF-508/JF-526 full-keyword coverage gate plus, on the cross-media routes, the higher 85 bar; not the same wrong-accept class observed live). Known trade-offs, deliberate: the collision check itself is UNIFORM on every path (review round F1: the candidate's Double Metaphone codes come from the pinned index when available and are encoded from the candidate name at the decision point otherwise, so the bar applies identically on the cold-index database path); only the near-tie RUNNER-UP detection remains a cold-window skip, because it needs the full in-memory pool (the database path has none; same cold-window trade-off class as the JF-381 Fast-mode exception). A stale kana flag on PlayAlbum's calling-word-stripped musician (flag captured at entry, value may be replaced by a Latin stripped title later) errs STRICTER, never toward a wrong auto-play.

REVIEW ROUND (2026-09-27, same sitting as the commit): F1 removed the score-band floor test entirely (a bare score >= 91 proves no collision: plain PartialRatio reaches 91-99 for near-identical strings); the bar now gates on real Double Metaphone codes via ArtistSearch.PassesKanaOriginCollision/PassesKanaOriginAcceptance, and IsPhoneticFloorScore is deleted. F2 extracted the duplicated machinery: ArtistSearch.FindNearTiedRunnerUp (the near-tie scan, one implementation for both decision points), DisambiguationHelper.AskMultipleArtists (the JF-420.2 ask, extracted FROM the pre-existing JF-420 block, now serving three sites), ArtistSearch.ScoreBestWithCodes (the pinned-index scoring ternary), and the single PassesKanaOriginAcceptance predicate. Unchanged per review: the three gate placements, the <= 5 tie rule as separate from JF-420, and the accepted [threshold, 90] kana band loss (failure direction is not-found, never wrong-play).

TESTS: `Jellyfin.Plugin.AlexaSkill.Tests/Handler/KanaOriginAcceptanceTests.cs`, 7 cases pinning all four shapes: DM-colliding pair (Queen/Keane, production DoubleMetaphone codes) -> the disambiguation ask naming both, never auto-play (handler AND cross-media); plain-fuzzy-only ('ビートルズ' vs Sator) -> honest not-found / null (handler AND cross-media); single clear DM-collision winner -> auto-play; plus the Latin byte-identical control (`PassesArtistMatchAcceptance` containment 90 passes with kanaOrigin false, refused with true). No existing test modified (the full pre-existing suite is the Latin regression matrix).

VERIFICATION (this worktree): `dotnet build Jellyfin.Plugin.AlexaSkill.sln` 0 errors 0 warnings; `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`: `Passed!  - Failed: 0, Passed: 4505, Skipped: 0, Total: 4505` on BOTH net9.0 and net10.0 (4498 pre-existing + 7 new). No thresholds, config flags, or locale strings changed. Live re-verification battery (simulator: クイーン -> Queen or the disambiguation; ビートルズ -> Beatles or not-found, never Sator; genre rows unchanged) runs post-merge/deploy.
