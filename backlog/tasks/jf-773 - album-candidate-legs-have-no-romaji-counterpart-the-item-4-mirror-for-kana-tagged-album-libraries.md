---
id: JF-773
title: >-
  JF-773 - album candidate legs have no romaji counterpart: the item-4 mirror for
  kana-tagged album libraries (permanent narrowing, no index to close it with)
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-755
references:
  - >-
    backlog/tasks/jf-755 - symmetric-index-side-kana-normalization-kana-tagged-libraries-and-the-kana-canonical-shape-plus-the-normalizer-chain-trigger.md
priority: low
---

## Description

Filed 2026-10-05 from the JF-755 /simplify altitude round (out of that diff's
declared scope, per the file-same-turn rule). JF-755 closed the JF-643
query-side-only narrowing on the two IN-MEMORY indexes (artists: parallel romaji
key + romaji-derived phonetic codes; songs: TitleTokens token union in the index
build and the scorer). The ALBUM surfaces have the same query-side romanization
(AlbumPlayService romanizes the slot at ~line 440, PlayAlbumIntentHandler at
~line 116) with NO candidate-side counterpart, and unlike the artist/song
cold-window residual this narrowing is PERMANENT, because albums have no
in-memory index to carry a romaji key: every album candidate leg scores raw
names (AlbumPlayService's FindBestNonEmbeddedMatch around line 482,
PlayAlbumIntentHandler's around line 608, both `a => a.Name`). A kana-tagged
album library (a J-pop library with albums titled カタカナ) is unreachable by
both kana and romaji queries on every album path, which is the item-4 mirror of
the JF-645 split for albums: if kana-tagged libraries matter for artists, they
matter for albums.

The cheap general fix when picked up is the SCORE-TIME shape JF-755 already
shipped for songs: route the album candidate legs' title-side comparisons
through KeywordMatcher.TitleTokens (the union helper) or a QueryNameFor-shaped
resolver over a per-query romanization, NOT a new album index (there is no
album index to extend, and building one for this is not justified). The album
kana ACCEPTANCE-BAR work (the JF-652 mirror on the album fuzzy arm) is
separately tracked in JF-661/JF-662/JF-663; this task is only the reachability
leg.

Note: the JF-755-refreshed KatakanaRomanizer class doc carries a pointer here
(the "ALBUM surfaces sit in that narrowing PERMANENTLY" paragraph); when this
lands, update that paragraph alongside.

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] dotnet build passes with 0 errors
- [ ] dotnet test passes
- [ ] No new compiler warnings introduced
- [ ] Kana-tagged album reachable by a romaji query (red proof at the album fuzzy arm layer)
- [ ] Latin album behavior byte-identical (control pin)
- [ ] KatakanaRomanizer doc paragraph updated in the same change
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
- Do NOT build an album index for this; the score-time union is the shape.
- The JF-661/662/663 album kana bars read candidate names for collision checks;
  they stay on raw names unless the reachability change makes the romaji form
  the honest collision surface there too (decide with the JF-659-invariant doc
  in hand, the same weighing JF-755 recorded).
<!-- SECTION:NOTES:END -->
