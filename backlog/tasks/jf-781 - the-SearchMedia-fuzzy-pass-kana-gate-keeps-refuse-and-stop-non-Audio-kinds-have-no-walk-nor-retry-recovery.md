---
id: JF-781
title: >-
  JF-781 - the SearchMedia fuzzy-pass kana gate (PassesKanaSongGate) keeps
  refuse-and-stop: non-Audio kinds have no walk and no song-title-retry recovery
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-777
references:
  - >-
    backlog/tasks/jf-777 - the-song-side-single-point-kana-bars-refuse-and-stop-shadow.md
priority: low
---

## Description

Filed 2026-10-05 from the JF-777 /simplify altitude round (out of that task's
declared surface: the filing names SearchMedia's PRE-CHECK, not the fuzzy-pass
gate one branch earlier; a conversion there is a behavior change that needs its
own red proof).

The JF-777 walk conversion taught `SearchItemsFuzzyAsync` the `acceptanceBar`
parameter and converted the playlist caller, but SearchMediaIntentHandler's OWN
fuzzy-pass calls to that same method (~lines 186 and 189,
"SearchMediaFuzzyFallback" / "SearchMediaFuzzyOutOfLibrary") still apply the
kana bar POST-CALL on the single returned winner via `PassesKanaSongGate`
(~line 460): the refuse-and-stop single-pick idiom the walk exists to replace,
in the same file that motivated it.

The surviving shadow (the exact JF-777 shape): a kana-tagged library holding
BOTH the exact item and a space-separated suffixed sibling whose reading
contains the query, the scan lists the sibling first (the containment-class
early exit, the JF-427 note), the gate refuses it, and `results` stays empty.
Audio recovers through `TrySongTitleRetry` (the song n-gram index +
`FilterByContentAccess(new[] { Audio })`, verified at ~444-475), but the retry
is Audio-ONLY: for every other playable kind (Movie, Episode, Series, Playlist,
AudioBook, MusicAlbum) the honest `MediaNotFound` fires with the exact item in
the library. The JF-777 pins all use song fixtures, so every red proof
recovered via the retry and no test sees the gap.

Fix shape: pass `acceptanceBar: item => PassesKanaSongGate(item, query, locale,
kanaOrigin)` (guarded to kana as today) on both fuzzy calls; the predicate
already computes its own KeywordMatcher score internally, so the score-less bar
signature suffices; the walked-out case keeps today's fall-through to the
song-title retry unchanged; dedupe the resulting double refusal log
(PassesKanaSongGate's own line vs the walk's line) so the triage surface stays
one line per refusal.

## Definition of Done

- [ ] Red proof: a kana-tagged VIDEO (or Series/Playlist) library with the
      suffixed sibling listed first in the fuzzy scan, the exact item plays
      (kana query); pre-change tree first, both TFMs
- [ ] The existing JF-654 fuzzy-pass bait pin stays green (the bait alone still
      falls through to the Audio-only retry and the honest miss)
- [ ] dotnet build 0 errors, dotnet test green, no new warnings

## Implementation Notes

<!-- NOTES:BEGIN -->
- The JF-777 KatakanaRomanizer class-doc paragraph already scopes the walk
  closure to the song/playlist bars; this site's conversion updates that
  sentence's "song-side" list with the fuzzy-pass gate.
- `PassesKanaSongGate` uses `ScoreWithPhoneticFallback` (phonetic-stage aware)
  while the pre-check bar uses `KeywordMatcher.Score`; keep whichever the
  conversion preserves when the predicate moves inside the walk.
<!-- NOTES:END -->
