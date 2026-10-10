---
id: JF-781
title: >-
  JF-781 - the SearchMedia fuzzy-pass kana gate (PassesKanaSongGate) keeps
  refuse-and-stop: non-Audio kinds have no walk and no song-title-retry recovery
status: Done
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

<!-- SECTION:DESCRIPTION:BEGIN -->
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
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Red proof: a kana-tagged VIDEO (or Series/Playlist) library with the
      suffixed sibling listed first in the fuzzy scan, the exact item plays
      (kana query); pre-change tree first, both TFMs
      (DONE 2026-10-05: three red pins on the UNMODIFIED tree, both TFMs
      net9.0/net10.0, each "Failed: 3, Passed: 1, Total: 4" in
      SongKanaBarRefuseAndContinueTests - the Movie pin (video launch), the
      MusicAlbum pin (audio launch, primary call), and the restricted-user
      Playlist pin (the SearchMediaFuzzyOutOfLibrary sibling call, so BOTH
      fuzzy calls' bars are armed and pinned); the fourth pin, the bait-alone
      control, was green pre-fix by design and stays green)
- [x] #2 The existing JF-654 fuzzy-pass bait pin stays green (the bait alone still
      falls through to the Audio-only retry and the honest miss)
      (DONE 2026-10-05: HandleAsync_KanaQuery_FuzzyPassSoupHit_GatedToHonestNotFound
      green in the 88-test kana/SearchMedia neighborhood battery and in the full
      suite; the new bait-alone control pins the same invariant at the walk)
- [x] #3 dotnet build 0 errors, dotnet test green, no new warnings
      (DONE 2026-10-05: full suite 5307/5307 both TFMs (main baseline ~5303 +
      4); Debug build 0 warnings; Release -warnaserror clean)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
- The JF-777 KatakanaRomanizer class-doc paragraph already scopes the walk
  closure to the song/playlist bars; this site's conversion updates that
  sentence's "song-side" list with the fuzzy-pass gate.
- `PassesKanaSongGate` uses `ScoreWithPhoneticFallback` (phonetic-stage aware)
  while the pre-check bar uses `KeywordMatcher.Score`; keep whichever the
  conversion preserves when the predicate moves inside the walk.
- LANDED SHAPE (2026-10-05): both SearchItemsFuzzyAsync calls take
  `acceptanceBar: fuzzyPassBar`, ONE local armed under `if (kanaOrigin)` (the
  family idiom: this handler's pre-check `songBar`, CrossMediaFallback's
  `songBar`, AlbumPlayService's `playlistBar`); the closure hoists the query's
  tokens and DM codes once (the encode-once rule) and `PassesKanaSongGate`
  became the codes-carried predicate over `PassesKanaOriginSongAcceptance`,
  keeping `ScoreWithPhoneticFallback` per Implementation Note 2. One semantic
  edge kept byte-identical to the pre-conversion gate: a pick the
  KeywordMatcher chain does not even admit (empty scored list) is a REFUSAL,
  not a score-0 collision acceptance (the pre-check bar's shape was
  deliberately NOT adopted here; adopting it would widen acceptance). The
  gate's own refusal log line was removed (the filing's dedupe rule): the
  walk's shared Information line ("{Op}: acceptance-bar refusal ... walking
  down the ranking") is the one line per refusal, and the walked-out scan has
  its own exhaustion line.
- GATES: /simplify (4 agents) applied/skipped - APPLIED the hoisted bar +
  encode-once closure + codes-carried predicate (consensus of all four
  angles), the registry-doc trims (SearchService param doc and
  KatakanaRomanizer class doc now carry caller identity only; the rationale
  lives at the call site), the TestHelpers.CreateMovie hoist (the fourth
  private construction crossed the third-copy rule), the SetupFuzzyScanOnly
  onlyKind parameter (the playlist pin reuses the helper), and the
  single-negative no-directive assert; SKIPPED repointing the pre-existing
  per-suite private Movie factories (out-of-diff churn; the helper's doc
  notes they repoint as touched), and SKIPPED sharing one token/codes pair
  between the fuzzy-pass bar and the pre-check bar (code-review F1: the fix
  would hoist derivation to every request including primary-search hits,
  moving cost onto the common path, and churns untouched lines; the duplicate
  is once per REQUEST, not per candidate - the per-candidate hoist is the one
  that mattered and is applied). /code-review high: no correctness defects;
  F3 applied (a banned parenthetical hyphen in a test comment); F1 skipped as
  above; F2 skipped (the mock's hardcoded Limit==500 matches the suite's
  established raw-shape idiom - the JF-654 bait pin hardcodes the same shape -
  and SearchItemsFuzzyAsync carries no named constant to reference; a Limit
  change detaches the mock into a LOUD red, not a silent pass).
- OBSERVATION (Altitude agent, non-blocking, recorded not filed): the
  acceptanceBar walk has no seam-level pin on SearchItemsFuzzyAsync itself;
  the mechanism is pinned transitively at every live caller (playlist
  JF-777, the SearchMedia pre-check JF-777, this fuzzy-pass gate JF-781), so
  no uncovered behavior exists and no task was cut for it.

FOLDED FROM RAW TAIL (2026-10-10, the JF-853 normalize-then-fold; content verbatim, previously outside the managed sections):

CLOSED 2026-10-05 by the orchestrator: merged into main at 6a2338d5, gate-marker six axes CLEAN, production deploys with the next batch.
<!-- SECTION:NOTES:END -->
