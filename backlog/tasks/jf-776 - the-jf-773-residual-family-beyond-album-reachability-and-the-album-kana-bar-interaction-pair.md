---
id: JF-776
title: >-
  JF-776 - the JF-773 residual family: the romaji-mirror class beyond albums
  (SearchItemsFuzzyAsync string legs) and the album kana bars' interaction pair
  (shadowing walk, full-width parenthetical strip)
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-773
references:
  - >-
    backlog/tasks/jf-773 - album-candidate-legs-have-no-romaji-counterpart-the-item-4-mirror-for-kana-tagged-album-libraries.md
priority: low
---

## Description

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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] dotnet build passes with 0 errors
- [ ] dotnet test passes
- [ ] No new compiler warnings introduced
- [ ] Section A: kana-tagged candidate reachable by a romaji query at the
      SearchItemsFuzzyAsync layer (red proof on the pre-change tree first,
      both TFMs), plus one playlist-surface and one audiobook-surface handler
      red proof
- [ ] Section A: Latin behavior byte-identical (control pin)
- [ ] Section A: the JF-663 playlist bar's collision input moves in the same
      change as the playlist candidate legs (the coupling above)
- [ ] Section A: HandleFuzzyMiss callers that adopt ScoringName use the
      speechSelector seam (speech keeps the display name)
- [ ] Section B1: the shadowing red proof (suffixed sibling listed first)
      plays the exact album; the JF-661/JF-662 bait pins stay green
- [ ] Section B2: the full-width parenthetical form plays through the album
      bar; the ASCII strip matrix is unchanged
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
