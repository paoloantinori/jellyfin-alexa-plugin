---
id: JF-777
title: >-
  JF-777 - the song-side single-point kana bars keep refuse-and-stop: the same
  order-dependent shadow JF-776 B1 closed on the album arms
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-776
references:
  - >-
    backlog/tasks/jf-776 - the-jf-773-residual-family-beyond-album-reachability-and-the-album-kana-bar-interaction-pair.md
priority: low
---

## Description

Filed 2026-10-05 from the JF-776 implementation (the filing's B1 "consider the
same weighing for the song bars so the family stays coherent" clause, weighed
and deferred: each song site has its own acceptance shape and its own pinned
suite, so bundling them into JF-776 would have made that task's verification
bar ambiguous about which surface a red proof covered).

The JF-776 B1 fix converted the two ALBUM arms' kana bars from refuse-and-stop
to the JF-412 refuse-and-continue walk (the bar rides
`CrossMediaFallback.FindBestNonEmbeddedMatch` as an acceptance predicate). The
SONG-side single-point bars kept their refuse-and-stop shape, so the same
order-dependent shadow survives there, now REACHABLE for kana-tagged names
(JF-776 section A made the string-level song legs reading-aware):

- `CrossMediaFallback.TrySongFallback` (~line 558): `scored[0]` acceptance via
  `SongIndexSearch.PassesKanaOriginSongAcceptance`; a refused head aborts the
  fallback instead of walking `scored[1..]`.
- `SearchMediaIntentHandler`'s site-level full-coverage pre-check (~line 245):
  a refused `topMatch` answers the honest `MediaNotFound` instead of re-running
  the `FuzzyMatch` pick on the remainder.

The shadow shape (both sites): a kana-tagged library holding BOTH the exact
song and a SPACE-SEPARATED suffixed sibling whose reading contains the query
('ヨルニカケル' + 'ヨルニカケル デラックス' for query 'ヨルニカケル'). The
single-token suffixed form ('ヨルニカケルデラックス') does NOT shadow: it fails
the full-keyword-coverage gate first (the query token is not one of the fused
title's tokens), so it never reaches the bar at the SearchMedia pre-check. The
DB/scorer listing order decides which sibling the walk returns, and the
refuse-and-stop discards the exact match behind it. Narrower than the album
case (the coverage gate pre-filters the fused form) but the same class.

## Definition of Done

- [ ] Red proof per site: the space-separated suffixed sibling listed FIRST,
      the exact song plays (kana query); pre-change tree first, both TFMs
- [ ] The existing JF-654 song-bar bait pins stay green (a bait with no
      alternate still lands the honest not-found)
- [ ] The list-narrowing forms (`ApplyKanaOriginBar`) are confirmed unaffected
      (they already walk-equivalent; no change expected)
- [ ] dotnet build 0 errors, dotnet test green, no new warnings

## Implementation Notes

<!-- NOTES:BEGIN -->
- Fix shape: mirror the JF-776 B1 pattern at each site (remove the refused
  winner, re-run the pick), NOT a new shared walker: the two sites pick through
  different machinery (a scored chain's head vs a FuzzyMatch over a list), so a
  forced unification would be a refactor beyond the fix.
- The SearchMedia pre-check walk must keep falling through to HandleFuzzyMiss
  on a withheld (coverage-failed) pick; only the BAR refusal walks.
- KatakanaRomanizer's class doc already points here (updated by JF-776).
<!-- NOTES:END -->
