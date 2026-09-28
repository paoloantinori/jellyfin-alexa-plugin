---
id: JF-662
title: >-
  JF-662 - PlayAlbum's own JF-336 fuzzy-album arm has no kana bar: the 60-bar
  sits ONE gate before the JF-660-fixed entity fallback
status: Done
assignee: []
created_date: '2026-09-28 18:10'
updated_date: '2026-09-28 19:21'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
dependencies: []
references:
  - >-
    backlog/tasks/jf-660 -
    JF-660-JF-652-artist-side-kana-bar-inert-on-PlaySong-FindSong-entity-fallback-both-pass-pre-romanized-text-to-TryEntityFallbackAsync.md
  - >-
    backlog/tasks/jf-661 -
    JF-661-the-song-to-album-cascade-has-no-kana-bar-PlaySong-not-found-reroutes-a-kana-origin-query-into-TryAlbumFallbackAsync-which-plain-accepts-at-90.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-660 /code-review high round (finding 1), verified against PlayAlbumIntentHandler the same turn.

THE GAP: JF-660 threads the kana-origin flag into the entity fallback (TryEntityFallbackAsync) and computes `albumKanaOrigin` at PlayAlbum entry. But PlayAlbum's OWN in-handler fuzzy album arm (JF-336, PlayAlbumIntentHandler's `FindBestNonEmbeddedMatch(album, allAlbums, a => a.Name!, FuzzyMatcher.GetDefaultThreshold(user))`, the block guarded by `MinFuzzyAlbumQueryLength`) accepts at the BARE default threshold (60) with NO kana gate and NO Double Metaphone evidence, and it is the FIRST fuzzy acceptance point a kana album miss flows through, sitting one gate BEFORE the artist fallback JF-660 barred. A ja-JP album miss ('ビートルズ' romanized to 'bitoruzu', 8 chars, over the min-length guard) that plain-fuzzy matches a short Latin album name at >= 60 (the 'Bitters'-class bait, exactly the 60-class the JF-660 PlaySong tests refuse on the artist arm) auto-plays with FoundAlbumInstead and never reaches the entity fallback. `albumKanaOrigin` is already in scope at the arm; it is unused there.

NOT a duplicate of JF-661: that task tracks the song-to-album CASCADE (AlbumPlay.TryAlbumFallbackAsync, the 90-bar, reached from PlaySong's not-found); this is the in-handler 60-bar arm inside PlayAlbumIntentHandler, a different acceptance point at a different threshold.

FIX SHAPE: gate the arm's acceptance on the kana-origin flag (reuse `albumKanaOrigin` from the JF-660 capture; for a kana-origin query require the same evidence class the other kana bars demand, a real Double Metaphone collision via the artist-side PassesKanaOriginAcceptance shape or a near-exact plain score, re-derived for the album score scale) so the plain-fuzzy 60-class auto-play becomes the honest album not-found.

VERIFICATION BAR: a kana-origin PlayAlbum album=ビートルズ miss over a library whose albums include a 'Bitters'-class plain-fuzzy bait ends in the album not-found, never a wrong album auto-play; the Latin fuzzy-album matrix (JF-336's accent/spelling class, 'caffè' vs 'Cafe') unchanged.
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
<!-- SECTION:NOTES:BEGIN -->
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-28 as part of merge 370e7662 (pushed; deployed; live battery green: PlayAlbum album=ビートルズ -> the honest album not-found, the bait arm dead; the Latin thriller control unchanged): PlayAlbum's own JF-336 fuzzy arm (the bare 60-bar in-handler FindBestNonEmbeddedMatch, one gate before the entity fallback - the fourth ungated surface the JF-660 review found) now applies the shared collision predicate when albumKanaOrigin is true (head-check only, the JF-654 rule). The doctrine criterion recorded in both tasks' notes: substitution speech demands collision evidence; direct literal-index plays ride the server's exact match. Gates: /simplify + code-review in-worker, the code-review skill marker in the orchestrator transcript (findings applied/dispositioned per the report), suites 4706/4706 both TFMs.
<!-- SECTION:FINAL_SUMMARY:END -->
