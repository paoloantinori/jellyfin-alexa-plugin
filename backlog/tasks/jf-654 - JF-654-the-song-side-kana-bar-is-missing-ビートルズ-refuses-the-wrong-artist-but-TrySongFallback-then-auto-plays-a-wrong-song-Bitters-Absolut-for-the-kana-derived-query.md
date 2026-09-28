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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-28 worker round (landed on the worktree branch):

THE EMPIRICAL DEVIATION (read this before touching the collision leg): the task
prescribed reusing PassesKanaOriginCollision with codes from the romanized
query and the song title. Verified against the production encoder BEFORE
writing the pins (scratch probe, then pinned by
KanaOriginSongAcceptanceTests.PassesKanaOriginSongAcceptance_CollisionLeg):
Double Metaphone codes cap at 4 characters and are first-word-dominated, so
'bitoruzu' AND 'Bitters & Absolut' BOTH encode to PTRS. The live wrong-accept
IS a full-string code collision; the bare JF-652 predicate would have ACCEPTED
it and failed the task's own verification bar. The bar's collision leg is
therefore LENGTH-BANDED: the codes must collide AND the romanized query and the
title must sit within KanaOriginSongCollisionLengthBand (= FuzzyMatcher
.PhoneticFloorLengthBand = 3, compile-level tie, the JF-381 lesson applied
verbatim: a collision between strings of wildly different lengths is not
accent-drift evidence because the code cap collapses unrelated strings into
the same skeleton). Legit classes verified surviving: 'クイーン' -> 'kuin' vs
'Queen' (codes collide, 4 vs 5 chars, in band); 'bohemian rapusodi' vs
'Bohemian Rhapsody' (codes collide, 17 vs 17, in band). Half-title shapes
('rapusodi' vs 'Bohemian Rhapsody') fall outside the band and take the honest
not-found, the same strictness trade JF-652 accepted on the artist side.

THE BAR (one shared definition, SongIndexSearch.PassesKanaOriginSongAcceptance
+ the ApplyKanaOriginBar list form; the JF-440 one-chain file is the home,
mirroring how JF-652's bar lives in ArtistSearch): for a kana-origin query,
auto-play requires a real (length-banded) DM code collision between the
romanized query and the song title OR a >= 95 near-exact plain score. On the
KeywordMatcher coverage scale 95 means every query keyword is a VERBATIM title
token AND >= ~2/3 of the title is covered; the phonetic stage's ceiling is
75+5+10 = 90, so the plain leg admits exact-stage matches only. The existing
CrossMediaSongThreshold (65) gate at TrySongFallback still runs FIRST and
untouched; the kana bar composes after it.

PER-SITE DECISIONS (apply vs leave, with reasons):
- CrossMediaFallback.TrySongFallback acceptance: APPLY. The live-exposed
  wrong-accept (deployed a9c57451): 'bitoruzu' over the 65 bar against
  'Bitters & Absolut', auto-played with FoundSongInstead. The bar checks the
  HEAD candidate only, deliberately documented: this is a fallback GUESS on a
  coin-flipped slot; walking the ranking to serve a lower-scoring collision
  carrier is a deeper guess (the list-walking form belongs to the primary
  search paths).
- PlayArtistSongsIntentHandler, both call sites (the not-found path and inside
  ApplyKanaOriginAcceptance): APPLY (thread the flag; the JF-652 downgrade path
  re-enters the song fallback, kana true by construction there).
- QueryArtistLibraryIntentHandler call site: APPLY (sibling coverage, the JF-440
  same-coin-flip comment; flag computed at the call on the raw slot, this
  handler never romanizes its own copy).
- FindSong SearchAndRespondAsync: APPLY. The single-match auto-play had NO
  score bar at all (songs.Count == 1 -> FoundOne); a kana keywords query
  phonetic-colliding with exactly one song auto-played it. One filter point
  after the branch merge covers ALL THREE stages (artist-scoped, n-gram, DB
  fallback) and feeds the honest FindSongNoMatch re-prompt on a miss. The 1-4
  disambiguation list also only ever speaks bar-passers now.
- PlaySong title fallback (both JF-383/JF-384 branches): APPLY. The
  fallback-filled candidates fed the count==1 auto-play and the HandleFuzzyMiss
  >= 90 auto-accept with no numeric bar of their own.
- PlaySong PRIMARY SearchTerm path: LEAVE. A server-side index match on a
  romanized string is literal, not the fuzzy wrong-accept class.
- SearchMedia song-title retry (TrySongTitleRetry): APPLY. It is the same
  JF-440 chain ("the same chain PlaySong's title fallback uses" per its own
  comment) and its hits feed the single-result auto-play (deduped[0] ->
  PlayItem).
- SearchMedia primary SearchTerm / fuzzy pass / artist fallback: LEAVE. The
  primary is literal; the fuzzy pass and artist-items auto-play are the JF-652
  artist-side machinery, whose decision points JF-652 scoped without
  SearchMedia (the artist-side latent gap found during this task's review is
  filed separately as JF-660, not fixed here: the dispatch forbids touching
  the artist bar).
- JF-652 artist bar, genre tier, Latin thresholds/coverage values: NOT TOUCHED.

FLAG THREADING: TrySongFallback gained `bool kanaOrigin = false` (last param);
all 3 call sites pass it, computed via ArtistSearch.IsKanaOriginQuery
(canonicalMusician, rawMusician) where a canonical exists (the JF-659
canonical-skip composes: an ER-resolved musician never reaches the fallback,
the call sites guard canonical == null, so kanaOrigin=true always means the
raw/kana shape). The free-text slots (FindSong keywords, PlaySong song,
SearchMedia query) have no ER canonical ever, so the flag is IsKanaOriginQuery
(null, raw) captured BEFORE each site's own romanization erases the script
evidence (the ordering constraint is stated at each capture).

GATES + VERIFICATION TAIL: /simplify ran as 4 parallel cleanup agents; applied:
the compile-level constant tie to FuzzyMatcher.PhoneticFloorLengthBand
(visibility private -> internal), the encode-once codes-carried overload +
plain loop in ApplyKanaOriginBar (the per-candidate query re-encode the
efficiency round flagged), comment de-duplication (one rationale owner in
SongIndexSearch, short pointers at the consumers), the head-only-vs-list
semantics documented at TrySongFallback, the QueryArtistLibrary flag moved to
its only consumer, test-file reuse of TestHelpers.GetPlayDirective/
AssertNoAudioPlayDirective + the file's own SetupLibraryEmpty + the Latin leg
added to the BelowCoverageBar composition control. Skipped with reasons: a
KatakanaRomanizer.RomanizeWithOrigin composite for the capture-then-romanize
couplet (outside the diff's files; would split the pattern across the 5
canonical-bearing sites; noted in JF-660's fix-shape discussion), threading
kanaOrigin through ApplyKanaOriginAcceptance instead of the literal true
(defensible by construction, commented). /code-review high ran as the gate
fork on the final diff. Build 0 errors 0 new warnings (plugin
TreatWarningsAsErrors=true, so the green build proves it); full suite
4684/4684 PASSED both TFMs (net9.0 + net10.0; 4668 pre-existing + 16 new
KanaOriginSongAcceptanceTests, existing tests unmodified). The Latin matrix
controls: TrySongFallback/FindSong/PlaySong Latin same-bait plays. The live
simulator battery (musician=ビートルズ -> honest not-found; genre and artist
controls unchanged) is the orchestrator's post-merge step per the dispatch.

/code-review high round (7 findings, dispositions): (1) PlaySong's not-found
reroutes into the unbarred song-to-album cascade, which can plain-accept an
album at 90 for the same kana class -> filed as JF-661 (verified
AlbumPlayService has no kanaOrigin and gates at CrossMediaAlbumThreshold=90);
out of this diff's song-acceptance scope per the dispatch. (2)
TryEntityFallbackAsync's artist flag inert on the PlaySong/FindSong paths that
pass pre-romanized text -> filed as JF-660. (3) length band reads raw
song.Name so suffix titles are band-refused -> triaged: largely refuted; with
CodesEqual being exact equality, a long suffixed title ('Queen (Live at the
BBC)') fails the CODE check itself (first-word-dominated 4-char cap absorbs
suffix consonants, KNLV != KN), so the band is not what refuses it; the
residual class (title whose leading skeleton equals the query code plus a
consonant suffix, e.g. 'Queening') is narrow and sits inside the documented
JF-652 strictness trade. (4) multi-token kana queries structurally unreachable
via the collision leg -> refuted by the pinned
PassesKanaOriginSongAcceptance_MultiWordLegitKanaQuery_CollidesWithinBand:
first-word-dominated pairs ('bohemian rapusodi' vs 'Bohemian Rhapsody', both
BHMN, verified against the production encoder) do collide; the reviewer did
not run the encoder. (5) head-only TrySongFallback vs list form -> already
documented as deliberate in the method doc. (6) misleading
IsKanaOriginQuery(canonicalMusician, ...) inside the null-canonical guard ->
applied, passes null explicitly. (7) banned 'word - word' comment breaks ->
applied, two comment sentences reworded in SongIndexSearch.

Gate-marker review round 2 (7 findings, dispositions):
(1) APPLY, THE HOLE: SearchMedia's fuzzy pass auto-plays a kana-matched song
with no bar and runs BEFORE the gated retry; the previous in-line comment
calling the pass 'artist-side machinery' was wrong (the fuzzy scan covers the
playable kinds INCLUDING songs). Fixed: PassesKanaSongGate (the fuzzy score
rides the PartialRatio scale the bar's plain leg cannot trust, so the hit is
re-scored through the KeywordMatcher chain, then ApplyKanaOriginBar); a
refusal falls through to the gated song-title retry.
(2) APPLY: TrySongFallback now SELF-COMPUTES kanaOrigin from its input
pre-romanization (bool? param, default null, the TryEntityFallbackAsync
shape). QueryArtistLibrary drops its threaded arg (it passes the raw slot);
PlayArtistSongs keeps explicit threading at both sites because its input is
already romanized at entry (self-computation would see Latin and leave the
bar inert), named in the method doc.
(3) APPLY: the topMatch full-coverage pre-check (the third song auto-play on
SearchMedia) takes the same bar via KeywordMatcher.Score on the single item;
a refusal returns the honest MediaNotFound rather than falling into
HandleFuzzyMiss, whose >= 90 auto-accept would play the refused item.
(4) EVALUATED, DECIDED: the band input. Token-banding on the collision-bearing
prefix was REJECTED on the evidence: the live bait IS a first-word-dominated
collision, so 'bitoruzu' vs the 'Bitters' prefix would pass any token-band
(codes PTRS==PTRS, 8 vs 7 chars) and the wrong-accept would return. Applied
instead: trailing PARENTHETICAL groups are stripped before BOTH the band and
the codes ('(2011 Remaster)' is metadata, not phonetic content; the
first-word-dominated DM code cannot see it either). This satisfies both
required pins: 'bohemian rapusodi' vs 'Bohemian Rhapsody (2011 Remaster)'
(stripped 17 vs 17, codes collide) PLAYS; the bait ('Bitters & Absolut', a
consonant-bearing non-parenthetical suffix) still refuses. The residual
recall class (consonant-bearing non-parenthetical suffixes, e.g. a title
'Queening' for query 'kuin') stays the documented not-found, same JF-652
strictness trade.
(5) APPLY: KanaOriginSongCollisionLengthBand owns its value again (= 3) with
a cross-reference comment; FuzzyMatcher.PhoneticFloorLengthBand reverted to
private (the compile tie from the /simplify round is undone: the two bands
are semantically distinct and tune separately).
(6) APPLY: ApplyKanaOriginAcceptance takes the handler's kanaOrigin (before
the CancellationToken, CA1068); a future non-kana caller inherits false.
(7) (the round's remaining items were the F1/F3 scope above.)

Round-2 pins: PassesKanaOriginSongAcceptance_RemasteredTitle_
ParentheticalsStrippedForBandAndCodes (the F4 pair), and the two SearchMedia
end-to-end tests (fuzzy-pass soup hit gated to MediaNotFound; topMatch
full-coverage tribute pick gated to MediaNotFound). Session-note: the round
was interrupted by a transient API kill mid-test-writing; on resume the
half-applied state was found and completed (the PassesKanaSongGate doc block
had landed INSIDE TrySongTitleRetry's doc comment, the wrapped log line was
mis-indented, and the new bool param tripped CA1068; all repaired before
commit).
<!-- SECTION:NOTES:END -->

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
