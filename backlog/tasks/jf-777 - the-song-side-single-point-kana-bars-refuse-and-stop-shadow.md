---
id: JF-777
title: >-
  JF-777 - the song-side single-point kana bars keep refuse-and-stop: the same
  order-dependent shadow JF-776 B1 closed on the album arms
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-05'
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

- [x] Red proof per site: the space-separated suffixed sibling listed FIRST,
      the exact song plays (kana query); pre-change tree first, both TFMs
      (all THREE named sites plus the fall-through leg: TrySongFallback's
      scored-chain head, SearchMedia's pre-check, the playlist fuzzy-fallback
      head-check, and the strengthened survivors-ask pin; RED on the unmodified
      tree 4 failed / 2 controls-green per TFM, BOTH TFMs, green post-fix)
- [x] The existing JF-654 song-bar bait pins stay green (a bait with no
      alternate still lands the honest not-found) (KanaOriginSongAcceptanceTests
      green in the full suites; the new TrySongFallback bait-alone pin green on
      BOTH the unmodified and fixed trees)
- [x] The list-narrowing forms (`ApplyKanaOriginBar`) are confirmed unaffected
      (they already walk-equivalent; no change expected) (SongIndexSearch.cs is
      untouched by the diff; the ApplyKanaOriginBar pins and their
      FindSong/PlaySong/SearchMedia-retry consumers all green)
- [x] dotnet build 0 errors, dotnet test green, no new warnings (Release
      --no-restore -warnaserror clean; 5297/5297 net9.0 AND net10.0, the main
      baseline 5283 + 14 new proofs)

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

GATE-MARKER ADDENDUM (2026-10-05, from the JF-776 orchestrator review): this filing's scope widens by TWO legs and records a verification-debt note. (1) THE PLAYLIST HEAD-CHECK (AlbumPlayService ~:909): a THIRD single-point refuse-and-stop kana bar with the exact B1 shadow JF-776 just made reachable (the suffixed-sibling-first ordering refuses on the length band and NotFoundPlaylist is spoken although the exact playlist exists) - unconverted and previously untracked; the KatakanaRomanizer class doc's "only the song-side bars keep the shape" sentence is corrected by this addendum. (2) THE MIXED-FORM PIN: B2's documented half-width-open/full-width-close strip (the LAST-opener-of-either-form cut) has no pin - add one with the B2 matrix. (3) VERIFICATION DEBT: six of the eight HandleFuzzyMiss selector sites (AddToQueue, PlayNext, PlaySong, PlayVideo, SearchMedia, PlayPodcast) plus the PlayPodcast JF-640 guard leg have no per-surface red proof (5 of 11 changed sites tested); a regression reverting one passes the suite. The per-site pins ride this task's next pickup.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-05 as ONE task (all three walk sites share the B1 pattern, the
bar definition, and the red-proof machinery; no leg demanded a split).

**Leg 1, the walks (the filing's core):** all three single-point refuse-and-stop
bars converted to the JF-776 B1 refuse-and-continue walk. (a)
`CrossMediaFallback.TrySongFallback`: the scored chain arrives best-first, so
the walk is the index advance (skip a bar-refused head, threshold re-checked
per head); the encode-once codes-carried closure arms only when kana. (b)
`SearchMediaIntentHandler`'s full-coverage pre-check: a refused pick is removed
from `deduped` and the FuzzyMatch re-picks; the removal is also the
fall-through's filter (a refused bait can never ride into HandleFuzzyMiss's
auto-accept, the pre-fix site comment's doctrine), a coverage-WITHHELD pick
still falls through unchanged, and a walked-out list is the honest MediaNotFound.
(c) The playlist head-check rides `SearchItemsFuzzyAsync`'s NEW optional
`acceptanceBar` parameter (the FindBestNonEmbeddedMatch idiom on the DB-scan
pick): a refused winner is removed, the pick re-runs, the JF-508/JF-526
coverage gate keeps its own refuse-and-stop, and every other caller of the
method (bar null) is byte-identical.

**Leg 2, the mixed-form pin:** both mixed-width parenthetical directions
(half-open/full-close and the mirror) pin the LAST-opener-of-either-form cut
through the album bar, beside the B2 ASCII/full-width matrix.

**Leg 3, the verification debt:** seven per-surface pins
(`KanaTaggedHandleFuzzyMissSiteReachabilityTests`) cover the six HandleFuzzyMiss
selector sites (AddToQueue, PlayNext, PlaySong, PlayVideo, SearchMedia's
Confirm leg, PlayPodcast) and the PlayPodcast JF-640 guard leg, each
discriminating its site's revert to the raw-name selector; the podcast guard
pin isolates the guard from the miss block via a Latin-album raw-best bait.
The SearchTerm-fed pools are mock-wired (production kana rows cannot ride that
tier alone); the pins hold the flow-coupling contract the JF-776 conversion
exists for, which is exactly what rots on a revert.

**Gates:** /simplify (4 parallel angles; applied 9: the survivors-pool collapse
onto direct `deduped` removal, the loop-invariant hoists (Tokenize/threshold)
at both walk loops, the positive-guard restructure in SearchItemsFuzzyAsync,
the mechanism-generic log tag (JF-663 dropped from the shared line), the
walked-out exhaustion Information legs at both walks, the KatakanaRomanizer
doc scoping (the artist-side single-point gates survive by design), the
SearchService cref de-fencing, the Song() factory delegation to
TestHelpers.CreateSong, and the SearchMedia pin's mock-wiring reuse; reasoned
skips: the ApplyKanaOriginBar substitution at TrySongFallback (it filters
silently, deleting the per-refusal triage log the family maintains; the task
mandates the B1 walk mirror), the shared walk primitive (the task's explicit
"NOT a new shared walker"), the songBar factory and the playlist mock-wiring
hoist (the house no-third-copy threshold, second copies today)).
/code-review high (5 findings, all applied: CR1 the AutoPlay-disjunct hole
closed by bar-judging inside the delegate (a partial-coverage survivor an
AutoPlay user's exemption surfaces now meets the bar; the discriminating pin
verified red against the unfixed delegate), CR2 the TrySongFallback exhaustion
log noise (empty pools keep the Debug leg; the Information line fires only
after refusals), CR3 the playlist site's spoken-form correlation restored as a
kana-miss Debug line plus the corrected comment claim, CR4 the survivors-ask
pin strengthened with positive assertions (now a 4th red proof on the
unmodified tree), CR5 the two parenthetical-hyphen comment lines swept).
Red proofs ran on the UNMODIFIED tree first (4 failed / 2 controls per TFM,
both TFMs), green post-fix. **Filed from this task:** JF-781 (the /simplify
altitude find: SearchMedia's own fuzzy-pass gate `PassesKanaSongGate` keeps
refuse-and-stop, and non-Audio kinds have neither the walk nor the Audio-only
song-title retry to recover). Suites: 5297/5297 both TFMs (the 5283 baseline +
14 new proofs); Release --no-restore -warnaserror clean. Not deployed (worker
branch only).
<!-- SECTION:FINAL_SUMMARY:END -->
