---
id: JF-661
title: >-
  JF-661 - the song-to-album cascade has no kana bar: PlaySong not-found
  reroutes a kana-origin query into TryAlbumFallbackAsync which plain-accepts at
  90
status: Done
assignee: []
created_date: '2026-09-28 12:15'
updated_date: '2026-09-28 19:21'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
  - device-found
dependencies: []
references:
  - >-
    backlog/tasks/jf-654 -
    JF-654-the-song-side-kana-bar-is-missing-ビートルズ-refuses-the-wrong-artist-but-TrySongFallback-then-auto-plays-a-wrong-song-Bitters-Absolut-for-the-kana-derived-query.md
  - >-
    backlog/tasks/jf-660 -
    JF-660-JF-652-artist-side-kana-bar-inert-on-PlaySong-FindSong-entity-fallback-both-pass-pre-romanized-text-to-TryEntityFallbackAsync.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-654 /code-review high round (finding 1), verified against AlbumPlayService the same turn.

THE GAP: JF-654 added the song-side kana bar at TrySongFallback, FindSong's chain, PlaySong's title fallback, and SearchMedia's song-title retry. But PlaySong's not-found flow then routes into the song-to-album cascade (AlbumPlay.TryAlbumFallbackAsync, JF-345), which has NO kana awareness: it romanizes at entry (AlbumPlayService.cs:352) and accepts at Math.Max(normal, CrossMediaAlbumThreshold=90). A kana-origin query whose songs were just refused can fuzzy-accept a short Latin ALBUM name at 90+ (plain PartialRatio reaches 90-99 for near-identical strings with no real collision; the JF-652 review established plain 91-99 does not prove collision) and auto-play it. The refused wrong-accept migrates one medium over instead of becoming the honest not-found.

Same family: JF-660 (the artist-side TryEntityFallbackAsync flag inert on the PlaySong/FindSong paths that pass pre-romanized text). Fixing both together makes sense: thread a kanaOrigin flag (or hoist the RomanizeWithOrigin composite on KatakanaRomanizer so raw capture cannot be bypassed) into TryAlbumFallbackAsync and gate its >= 90 acceptance with the JF-654 song bar shape (SongIndexSearch.PassesKanaOriginSongAcceptance, the length-banded collision OR >= 95 leg; note the album score scale may differ, re-derive the plain bar there).

VERIFICATION BAR: a kana-origin PlaySong song=ビートルズ miss ends in the song not-found, never an album auto-play without collision evidence; the Latin song-to-album cascade unchanged (JF-345 matrix).
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
Implemented 2026-09-28, commit 556773c5 (worktree branch, merged by the orchestrator). THREADING MAP: TryAlbumFallbackAsync gained the trailing bool? kanaOrigin (read pre-romanization; null self-computes via ArtistSearch.IsKanaOriginQuery, the JF-660 shape); the ONLY production caller PlaySong pins its existing JF-654 local (songQuery is the romanized local, self-compute would be inert); the test probe TestHelpers.CallTryAlbumFallbackAsync threads it for the gate-level pins. The acceptance bar is AlbumPlayService.PassesKanaOriginAlbumAcceptance: the length-banded DM collision via the ONE shared title-collision primitive (SongIndexSearch.PassesLengthBandedTitleCollision, generalized from the song-private method; no private strip+band+encode copy), collision-only with NO plain-score leg (the containment floor itself scores exactly 90; JF-652 killed score-band provenance on the FuzzyMatcher scale), composing over BOTH cascade tiers (the JF-652 acceptance-point precedent). SWEEP (the N-sites rule, every album acceptance site): (1) TryAlbumFallbackAsync 90-bar = GATED here; (2) PlayAlbum's JF-336 fuzzy arm 60-bar = GATED by JF-662; (3) PlayAlbum exact SearchTerm tier + JF-469 strip retry + JF-489/JF-492 musician-slot title retries = literal indexed acceptance, LEAVE (the JF-654 doctrine: a server-side index match on a romanized string is literal); (4) PlayAlbum JF-411 album-by-artist = acceptance is on the ARTIST (PassesArtistMatchAcceptance with musicianKanaOrigin, JF-652/JF-660-gated), album pick is deterministic most-tracks, LEAVE; (5) PlayAlbum multi-match disambiguation = sourced from literal tiers or the single JF-662-gated match, covered; (6) SearchMedia fuzzy pass over playable kinds incl. MusicAlbum = kana-gated by PassesKanaSongGate (JF-654); (7) SearchMedia primary SearchTerm = literal, LEAVE; (8) PlayPodcast MusicAlbum fuzzy fallback = JF-640 downgrades every album hit to the yes/no confirm, no silent auto-play, LEAVE; (9) BrowseLibrary fuzzy fallback = display list, no auto-play decision point, LEAVE; (10) BuildPlaylistPlayResponseAsync = playlist surface, not album, LEAVE. VERIFICATION TAIL: 5 pins in KanaOriginAlbumCascadeTests with production-matcher-dumped fixtures ('sato'/'Sator' 90 containment no ST/STR collision; 'bitoruzu'/'Bitoruzu Deluxe' 90 containment band 15v8; 'satoru'/'Satoru' identity collision; 'bitoruzu'/'Bitoruzu (Deluxe)' 90 with the parenthetical stripping to the colliding core); sensitivity check: with the bar disabled the two refusal pins fail and the play controls stay green. Signature note: request/cancellationToken became required (CA1068 under warnings-as-errors + CS1737); gates run: /simplify 4-agent round clean (3 optional findings dispositioned in the commit message), code-review high 7 findings (2 applied as doc/comment fixes, 5 rejected with documented precedents). Suites 4705/4705 both TFMs, build 0 warnings 0 errors.

GATE-MARKER REVIEW TAIL (2026-09-28, the coordinator's marker pass; finding 1, the playlist surface, was filed as JF-663 by the orchestrator). F3 APPLIED: the 'composes over BOTH candidate tiers' contract now has its pin, AlbumCascade_TierOneSearchTermWinner_PinnedKanaFlag_PlainFuzzyAlbum_ReturnsNull (the tier-1 MusicAlbum+SearchTerm query itself returns the 'Sator' bait for 'sato'; flag pinned returns null, never the substitution play), holding the contract against a future 'tier-1 winners are literal' doctrine drift. F2 KNOWN DIVERGENCE (recorded, no change): the album bar is collision-only while SearchMedia's fuzzy pass over MusicAlbum keeps the >= 95 plain leg (PassesKanaSongGate), so identical kana-origin queries can take opposite outcomes by entry surface; the containment-floor rationale drove the album shape (the containment floor itself scores exactly 90 on the FuzzyMatcher scale, so any plain leg would re-open the wrong-accept class, whereas on SearchMedia's KeywordMatcher scale >= 95 is near-exact by construction); the cross-surface consistency question is a future decision if a live case surfaces. F4 DOCTRINE CRITERION (recorded, no change): the line between the cascade's GATED tier-1 winner and PlayAlbum's ungated identical SearchTerm tier is: a CASCADE acceptance is a SUBSTITUTION (the FoundAlbumInstead announcement speaks a name the user did not say), so it is held to collision evidence; PlayAlbum's own SearchTerm tier is a DIRECT play riding the server's exact index match, the class PlaySong's primary path has kept ungated since JF-654. A future consistency pass must weigh that criterion before 'aligning' the two. F5 (house convention): the JF-663 playlist-surface test author is the natural fold-in consumer of the SetupAlbumTracks/album-factory fixtures this task family added (three per-file twins across PlaySongAlbumFallbackTests, KanaOriginAlbumCascadeTests, KanaOriginAlbumFuzzyArmTests; hoisting one shared helper is the target when a fourth file joins the family).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-28 as part of merge 370e7662 (pushed; deployed with the full checklist, config intact; live battery green: PlaySong song=ビートルズ -> the honest song not-found with NO album substitution; the PlayAlbum/クイーン regressions hold): the album-cascade kana bar. TryAlbumFallbackAsync gained the pinned-flag threading (PlaySong pins its JF-654 local; null self-computes) and the 90-bar acceptance now requires the length-banded DM collision (collision-only: the containment floor itself scores exactly 90, so no plain-score leg is provable on the FuzzyMatcher scale), composing over BOTH cascade tiers with the tier-1 contract pinned. The shared PassesLengthBandedTitleCollision primitive (generalized from the song-private shape) owns the strip+band+encode logic for song and album alike. Gates: /simplify + code-review in-worker, the code-review skill marker in the orchestrator transcript (no correctness bug; the tier-1 pin and the known-divergence/doctrine notes applied in the tail; the playlist surface filed as JF-663 same-turn), suites 4706/4706 both TFMs (orchestrator-verified at every round).
<!-- SECTION:FINAL_SUMMARY:END -->
