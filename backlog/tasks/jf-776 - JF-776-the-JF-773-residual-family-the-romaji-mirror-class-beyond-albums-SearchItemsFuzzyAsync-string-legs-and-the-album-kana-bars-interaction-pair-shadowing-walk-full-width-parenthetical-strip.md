---
id: JF-776
title: >-
  JF-776 - the JF-773 residual family: the romaji-mirror class beyond albums
  (SearchItemsFuzzyAsync string legs) and the album kana bars' interaction pair
  (shadowing walk, full-width parenthetical strip)
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-05 12:29'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-773
references:
  - >-
    backlog/tasks/jf-773 -
    album-candidate-legs-have-no-romaji-counterpart-the-item-4-mirror-for-kana-tagged-album-libraries.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-05 from the JF-773 implementation's review rounds (out of that
task's declared scope, per the file-same-turn rule). Two independent residual
classes surfaced; they share only their parent (JF-773's reachability change),
so the orchestrator may split them at triage. Every site below was verified
against the working tree, not assumed.

### Section A: the romaji-mirror class beyond albums

The SAME narrowing shape JF-773 closed for albums persists on every other
string-level fuzzy surface fed by an always-romanized query, tracked nowhere
else (backlog grep: kana/romaji x audiobook/video/podcast/channel,
SearchItemsFuzzyAsync):

**The choke point (closes most of the class in one edit):**
`SearchService.SearchItemsFuzzyAsync` romanizes its query at entry
(SearchService.cs ~320, JF-643) and then scores the bounded candidate scan
with the RAW selector `item => item.Name` (SearchService.cs ~359). Its own
comment lists the auto-playing consumers: PlayBook, PlayPodcast, PlayVideo,
PlayPlaylist, SearchMedia, SeriesFuzzyFallback (TvNextUpService); the call-site
grep adds PlayChannel, PlayRadio, BrowseLibrary. A kana-tagged name in any of
those libraries is unreachable by both the kana and the romaji spelling of its
name, permanently (no in-memory index exists for any of these surfaces). One
selector change (`KeywordMatcher.ScoringName` at that single site) closes the
whole SearchItemsFuzzyAsync class; the entry romanization already satisfies
the resolver's always-romanized-query invariant.

**The site-level legs after that:**
- PlayBookIntentHandler's own HandleFuzzyMiss delegate (`b => b.Name`, ~line
  133) and sibling handlers passing a raw display-name selector to
  HandleFuzzyMiss for SCORING: those need the JF-755 speechSelector seam
  (scoring selector resolves through ScoringName, speech selector keeps the
  display name), never a bare swap that would speak the romaji.
- SearchMediaIntentHandler's site-level `FuzzyMatch` pre-check (~line 230):
  string leg still raw. (Its coverage pre-check via
  `KeywordMatcher.HasFullKeywordCoverage` is already kana-aware through the
  JF-755 TitleTokens union; only the string FuzzyMatch leg is the gap.)

**Playlist surfaces (the concrete first member):** query side romanized at
entry (AlbumPlayService.BuildPlaylistPlayResponseAsync), candidate legs score
RAW names (the site-level `FuzzyMatch(playlistName, fuzzyCandidates, p => p.Name,
user)` ~line 931; the SearchItemsFuzzyAsync fallback ~line 868, closed by the
choke-point edit; the HandleFuzzyMiss delegate's `p => p.Name`). The JF-663
playlist bar (`PassesKanaOriginPlaylistAcceptance`) also reads the raw
playlist name; that is COHERENT today precisely because the matcher legs are
raw too, and the bar must move TOGETHER with the candidate legs (the
JF-755/JF-773 coupling: the collision input follows the reading the matcher
scores), never one without the other.

### Section B: the album kana bars' interaction pair (found by the JF-773 code review)

Both halves leave every library state no worse than pre-JF-773 (the kana-tagged
library was not-found before and stays not-found on these shapes; nothing
regresses), which is why they are filed rather than smuggled into the
reachability task: each fix is a bar-judgment change, and the AlbumPlayService
and CrossMediaFallback class docs reserve judgment-shape changes for dedicated
decisions (the JF-408 discipline).

**B1: head-check refusal can shadow an exact match behind a containment-class
reading.** `FuzzyMatcher.FindBestMatchWithScore` early-exits at the first
candidate scoring >= 90 (the containment class), so when a kana-tagged library
holds BOTH the exact album ('ヨルニカケル', reading 'yorunikakeru', exact 100)
AND a suffixed sibling whose reading contains the query
('ヨルニカケルデラックス' -> 'yorunikakeruderakkusu', containment 90), and the
DB returns the suffixed one first (GetItemList with no OrderBy, the JF-427
note), the walk returns the 90-score winner, the JF-661/JF-662 kana bar refuses
it on the length band (21 vs 12), the arms null out (refuse-and-stop), and the
user gets the album not-found although the exact album exists. Reachability on
JF-773's own target shape is order-dependent. Fix shape: refuse-and-continue at
both arms (re-run the FindBestNonEmbeddedMatch walk without the refused
winner, the JF-412 embedded-walk pattern) instead of refuse-and-null, updating
the "the bar judges the single best only (the JF-654 head-check rule, not a
JF-412 walk)" comments in AlbumPlayService.TryAlbumFallbackAsync (~line 509)
and PlayAlbumIntentHandler (~line 634); consider the same weighing for the song
bars so the family stays coherent (the playlist surface already narrows the
LIST, the walk-equivalent). Verification bar: a red proof with the suffixed
album listed FIRST (the mock's list order) asserting the exact album plays,
plus the existing JF-661/JF-662 bait pins staying green.

**B2: StripTrailingParentheticalGroups is ASCII-only, so the standard Japanese
full-width parenthetical form keeps its suffix and fails the band.** Album
'ヨルニカケル（デラックス）' romanizes to 'yorunikakeru（derakkusu）' (U+FF08/
FF09); the strip helper (SongIndexSearch.cs ~183) checks ASCII '(' and ')'
only, nothing strips, band |23-12| = 11 > 3, refused: the honest not-found.
The identically shaped Latin album 'Yorunikakeru (Deluxe)' strips, collides,
and plays. Pre-existing for Latin names carrying full-width parens too (not a
JF-773 regression), but JF-773 makes the strip load-bearing for kana readings.
Fix shape: teach the strip helper the full-width pair (and keep one
definition; the JF-654 "one title-collision semantics, one band" note).
Verification bar: the full-width form plays through the bar, the ASCII Latin
matrix unchanged.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: 0
      warnings, 0 errors, both TFMs)
- [x] #2 dotnet test passes (5275/5275 net9.0 AND net10.0; the main baseline
      5257 + 18 new proofs)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Section A: kana-tagged candidate reachable by a romaji query at the
      SearchItemsFuzzyAsync layer (red proof on the pre-change tree first,
      both TFMs), plus one playlist-surface and one audiobook-surface handler
      red proof (RED on the unmodified tree: 11 failed / 7 controls-green per
      TFM; the SearchService romaji+kana legs, the playlist kana+romaji legs,
      the book kana+romaji legs, the playlist bar coupling unit pin, both B1
      arms, both B2 legs; all green post-fix, 18/18 both TFMs)
- [x] #5 Section A: Latin behavior byte-identical (control pin:
      SearchItemsFuzzyAsync_LatinCandidate / PlayPlaylist_LatinPlaylist /
      PlayBook_LatinBook, all green on the unmodified tree AND post-fix; the
      5257 pre-existing tests stay green)
- [x] #6 Section A: the JF-663 playlist bar's collision input moves in the same
      change as the playlist candidate legs (the coupling above)
      (PassesKanaOriginPlaylistAcceptance resolves through ScoringName; the
      kana-query playlist proof needs BOTH halves and was red with either
      alone)
- [x] #7 Section A: HandleFuzzyMiss callers that adopt ScoringName use the
      speechSelector seam (speech keeps the display name) (all 8 adopting
      sites pass speechSelector: x => x.Name; the FuzzyMissHandler delegate
      threads the seam; the JF-773 review's speech assertions hold)
- [x] #8 Section B1: the shadowing red proof (suffixed sibling listed first)
      plays the exact album; the JF-661/JF-662 bait pins stay green (both
      arms pinned; KanaOriginAlbumCascadeTests / KanaOriginAlbumFuzzyArmTests
      green in the 5275)
- [x] #9 Section B2: the full-width parenthetical form plays through the album
      bar; the ASCII strip matrix is unchanged (unit + handler proofs; the
      ASCII matrix pin covers strip / stacked / non-parenthetical-widening)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
- Do NOT build any index for these surfaces; the score-time ScoringName shape
  is the fix (the JF-773 precedent, one resolver already in place at
  KeywordMatcher.ScoringName).
- Red proofs mirror KanaTaggedAlbumReachabilityTests (Tests/Handler): the
  kana-tagged item played from the romaji and the kana query, the Latin
  control, and a suffix-widening refusal pin surviving the romaji reading
  where a bar exists (playlist).
- The KatakanaRomanizer class doc names both residual classes as tracked
  here; update that paragraph when this lands.
- The two sections are independent; splitting this task at triage is fine
  (keep each section's verification bar with its half).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-05 as ONE task (both sections coherent: they share the
JF-773 parent, the same ScoringName resolver, and the same red-proof
machinery; no bar demanded a split).

**Section A (the romaji-mirror class beyond albums):** the ONE choke-point
edit; SearchService.SearchItemsFuzzyAsync's bounded scan scores through
KeywordMatcher.ScoringName (the JF-773 resolver; the entry romanization
already satisfies its always-romanized-query invariant), closing the whole
consumer family (PlayBook/PlayPodcast/PlayVideo/PlayPlaylist/SearchMedia/
SeriesFuzzyFallback/PlayChannel/PlayRadio/BrowseLibrary). Site-level legs:
SearchMedia's FuzzyMatch pre-check, the playlist surface's FuzzyMatch +
HandleFuzzyMiss delegate + the JF-663 bar's collision input (moved TOGETHER
per the coupling rule), and the eight HandleFuzzyMiss sibling sites
(PlayBook/PlayPodcast/PlayVideo/SearchMedia/AddToQueue/PlaySong/PlayNext +
the playlist delegate) scoring through ScoringName with speech kept on the
display name via the JF-755 speechSelector seam (threaded through the
FuzzyMissHandler delegate). PlayPodcast's JF-640 cross-type guard leg
converted too (the /simplify F1 flow-coupling find). No index built (the
filing's prohibition).

**Section B1 (the head-check shadow):** the album kana bars ride the JF-412
walk as an acceptance predicate (FindBestNonEmbeddedMatch's new optional
acceptanceBar parameter) at BOTH arms instead of refuse-and-stop; a refused
suffixed sibling no longer shadows the exact album listed after it, and a
bait with no alternate above threshold still lands the honest miss (the
JF-661/JF-662 pins green). The bar's query codes encode once and only when
armed (codes-carried PassesKanaOriginAlbumAcceptance overload, the
song/playlist encode-once idiom); the walk's refusal log keeps the display
name alongside the compared reading.

**Section B2 (the full-width parenthetical):** StripTrailingParentheticalGroups
(the ONE strip+band+encode primitive behind all three kana bars) strips the
U+FF08/U+FF09 pair with the same last-opener-of-either-form cut; the ASCII
matrix unchanged.

**Filed from this task:** JF-777 - the song-side single-point kana bars
(TrySongFallback's scored[0] acceptance, SearchMedia's pre-check) keep their
refuse-and-stop shape; section A makes their shadow reachable (narrower than
the album case: the fused-suffix form never passes the coverage gate), and
each site picks through different machinery, so the walk lands there as its
own task with the B1 red-proof template.

**Gates:** /simplify (4 parallel angles; 6 applied; the PlayPodcast guard
leg, the album-mock third-copy hoist into TestHelpers/fixture, the playlist
assertion-convention hoist, the shared FuzzyMissNotFound stub, the
codes-carried album bar, the caller-agnostic walk log; 3 reasoned skips -
the walk re-scan memoization (sub-ms on bounded pools, would change the
documented early-exit contract), the pre-check+HandleFuzzyMiss double pass
(pre-existing architecture, coupled threshold models), a named shared
selector pair (the inline pair IS the JF-755 convention)). /code-review high
(5 findings; 4 applied; CR2 the refusal log's display name + JF tag chain,
CR3 the encode-only-when-armed, CR4 the test-comment arithmetic, CR5 the
forwarder-layer removal; CR1 = the JF-777 filing, consciously accepted).
Red proofs ran base-compilable on the UNMODIFIED tree first (11 failed / 7
controls per TFM, both TFMs), all green post-fix. Suites: 5275/5275 both
TFMs (5257 baseline + 18); Release -warnaserror clean.

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker commit cf37a2bd + orchestrator tail bf9c472c, --no-ff; the gate-marker's seven axes PASS with the new suites re-run in the review tree; its five findings dispositioned: the hyphen forms and the walk comment applied, the playlist head-check/mixed-form pin/verification-debt filed as the JF-777 addendum), suites 5275/5275 both TFMs. Deploys batched with JF-778. JF-777 filed by this task.
<!-- SECTION:FINAL_SUMMARY:END -->
