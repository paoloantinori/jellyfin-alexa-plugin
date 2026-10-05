---
id: JF-755
title: >-
  JF-755 - symmetric index-side kana normalization (kana-tagged libraries and the
  kana-canonical shape) plus the normalizer-chain trigger for a second script
status: Done
assignee: []
created_date: '2026-10-04'
labels:
  - search
  - i18n
  - ja-JP
  - tech-debt
dependencies:
  - JF-645
references:
  - >-
    backlog/tasks/jf-645 -
    JF-645-JF-643-residuals-wire-the-remaining-kana-reachable-query-sites-lift-the-genre-tier-to-SearchService-at-first-sibling-vocabulary-TTL-cache-symmetric-normalization-for-kana-tagged-libraries.md
priority: low
---

## Description

Filed 2026-10-04 from the JF-645 split: items 1-3 of that filing (the remaining
kana-reachable query sites, the genre-tier lift to SearchService, the vocabulary
TTL cache) shipped as one coherent change; THIS task carries item 4 (symmetric
normalization) and item 5 (the script-coverage shape), which are one design
effort too large to ride along, per the JF-645 split reasoning.

ITEM 4, SYMMETRIC NORMALIZATION FOR KANA-TAGGED LIBRARIES (the JF-643 accepted
narrowing, recorded in KatakanaRomanizer's class doc): query-side-only
romanization means a kana query against a KATAKANA-TAGGED library name misses on
every tier where it previously exact-matched (romanized 'kuin' cannot equal the
kana name; Double Metaphone keeps kana, so the phonetic floor cannot rescue it).
If native-script-tagged libraries matter (J-pop libraries tagged クイーン in the
wild), the fix shape is romanizing kana-containing CANDIDATE text at
ArtistIndexService/SongNgramIndex build time (index kana names alongside their
romaji, preserving kana-kana exactness while still bridging kana-Latin) and the
matching legs in the matcher, per KatakanaRomanizer's KNOWN NARROWING note.

THE 2026-09-29 JF-658 REVIEW FINDING MOVES HERE (its tracked home was JF-645
item 4; recorded in JF-645's Implementation Notes): the JF-658 fold routed
PlayArtistSongs' ER canonical through ArtistSearch.SearchAsync's entry
romanization for the first time (the inline chain fed it verbatim). For a KANA
canonical (a kana-tagged artist in an otherwise Latin library, the mixed-library
shape) this both loses the pre-fold exact-self-match (the JF-643 narrowing,
already accepted) and OPENS reachability the inline chain never had: the
romanized query ('クイーン' -> 'kuin') can now weakly fuzzy-hit a LATIN artist
(the 'kuin'->Keane-at-91 class JF-652 was built to block) while the JF-652 kana
bar stays inert for canonical-bearing queries (the JF-659 invariant:
kanaOrigin=false whenever a canonical resolved, sound for Latin canonicals
because the ER match IS the collision evidence, unsound for kana canonicals
whose evidence points at the kana-named artist the romanized search can no
longer find). Pre-fold the same query missed every tier (honest not-found). Zero
test pins on the kana-canonical shape today (all canonical fixtures are Latin).
The symmetric index-side normalization this task tracks closes both legs at
once; the alternative scoped fix is a kana-canonical-specific bar for this
shape. NOT fixed inside JF-658 (behavior change beyond a no-behavior-change
consolidation fold) and NOT fixed inside JF-645 (its shipped subset is
coverage/consolidation/efficiency only, no matcher or index-build change).

ITEM 5, SCRIPT COVERAGE SHAPE (from JF-643's own Description, carried for
tracking): the romanizer is kana-specific by construction; Devanagari (hi-IN)
and Arabic (ar-SA) native-script values remain unmatched on every path. Nothing
to build until a second script is actually needed; when it is, extract a
normalizer-CHAIN shape (query-side script normalizers composed at the search
choke points) rather than growing a parallel one-off per script.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Debug both TFMs + Release --no-restore -warnaserror, 0/0)
- [x] #2 dotnet test passes (5235/5235 net9.0 AND net10.0; baseline 5207, +28 JF-755 tests, 0 broken pins)
- [x] #3 No new compiler warnings introduced (Release -warnaserror: 0 warnings)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (no session-attribute surface touched; the phonetic-code ValueTuple is the pre-existing IArtistIndex API, unchanged)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (no HttpClient surface touched)
- [x] #6 NLU test fixtures updated if interaction model changed (no interaction model, locale, or speech-key change; fixtures untouched)
- [x] #7 E2E test added for new intent or handler logic (handler-level pins in MusicianErCanonicalTests: the kana-canonical mixed-library play, the stale-catalog residual, the romaji-exact multi-match, the JF-377 romaji-containment ask; the SMAPI E2E layer needs the live skill and rides the orchestrator's wave)
- [x] #8 Locale response strings added to all 17 locales (no new response strings; every prompt reuses existing keys)
- [x] #9 /simplify passed (4 parallel angles; applied: the TryRomanize one-gate extraction, Concat+Except union, required index params, LoadAsync flattening, read-guard drops, test cleanups; skipped with recorded reasons: the Score/ScorePhonetic token-sharing refactor (1-3ms kana cold path, signature churn), the fixture hoists below/outside the diff bar)
- [x] #10 /code-review high passed (6 findings ALL applied: the JF-654 song-bar collision leg now reads the romanized title, KeywordMatcher.ScoringTokens replaces the diluting union in the coverage scorers, the HandleFuzzyMiss speech-selector seam separates scoring from speech, the JF-377/JF-420 gates judge the matched reading pair, the fake's codes/romaji invariant documented, TryRomanize rejects value-identical romanization; the album mirror filed same-turn as JF-773 from the simplify altitude round)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design constraints going in (from the JF-643/JF-652/JF-654 history, so the next
session does not re-derive them):
- Index-side romanization must NOT transliterate library values in place (the
  JF-643 decision 'candidates are never transliterated' holds for matching
  EXACTNESS); the shape is a parallel romaji key/name alongside the original,
  queried by the romanized query, with kana-kana exactness preserved (a kana
  query must still exact-match a kana name through the ORIGINAL key).
- The JF-652 kana-acceptance bar and the JF-654 song bar live at
  ArtistSearch/SongIndexSearch; the kana-canonical leg (the JF-658 finding
  above) needs either the symmetric index (canonical resolves to the kana-named
  artist again, and the JF-659 invariant becomes sound for kana canonicals) or
  its own bar; decide with the JF-659 invariant's doc in hand.
- The index services derive from DebouncedLibraryIndexService; the build-time
  change belongs in their load/refresh paths with the one-publish invariant
  (JF-448) respected.
- Warming-gate and choke-point layers (JF-419) are unaffected by index CONTENT;
  no gate changes should be needed.

DESIGN DECISION (2026-10-05): SYMMETRIC INDEX-SIDE NORMALIZATION (the filing's
option A), not the kana-canonical-specific bar. Weighed:
- Option A (symmetric index; CHOSEN): build-time parallel romaji keys.
  ArtistIndexService gains a Guid-to-romaji-name map for kana-containing names
  (exposed as IArtistIndex.TryGetRomajiName, one more published member of the
  JF-448 one-publish snapshot) and encodes those artists' Double Metaphone
  codes from the ROMAJI form (the kana-name code is empty garbage today: the
  encoder's Normalize keeps kana and the switch has no kana arm, so a
  kana-named artist can never collide, i.e. never pass the JF-652 bar even
  when reachable); SongNgramIndexService indexes the romanized token stream
  (bigram/single/phonetic) ALONGSIDE the kana stream; the matching legs use a
  parallel key, never an in-place swap (kana names keep flowing to JF-690
  exact-name resolution, speech, and the JF-377/JF-420 string gates). Artist
  string-shaped legs (tier-1 Contains, tier-2/3 prefix, tier-4 fuzzy selector,
  ScoreBestWithCodes, tier 1.5 tokenization) resolve through ONE resolver
  (ArtistSearch.QueryNameFor: the index's romaji name when present, else
  Name); song keyword-coverage legs (KeywordMatcher.Score, ScorePhonetic,
  HasFullKeywordCoverage) union the title token stream with the romanized one
  (TitleTokens, one definition). Failure modes carried: warm/cold divergence
  (the DB tiers search Jellyfin's own index, which has no romanizer, so a
  cold-window kana query still honestly not-founds, the accepted JF-381/JF-417
  DB-divergence class); interface growth (every IArtistIndex implementer and
  test fake gains a member); bounded memory (one extra map entry plus one
  extra token stream per kana-containing name only; Latin libraries
  byte-identical).
- Option B (a kana-canonical-specific bar; REJECTED): it cannot satisfy the
  red proof's second half. Flipping kanaOrigin to true for kana canonicals
  does NOT block the weak hit: the 'kuin'->Keane pick PASSES the JF-652 bar
  (threshold 60, real KN code collision) and only adds the near-tie prompt,
  so the user gets asked "Queen or Keane?" for a query whose ER evidence
  points at クイーン, an artist in neither option; a hard-reject variant
  restores the pre-fold honest not-found but leaves the kana-named artist
  unreachable, which is exactly item 4's defect, and it adds a third
  kanaOrigin state at every threading site. Only the symmetric index makes
  the romanized canonical EXACT-hit the kana artist's romaji key at tier 1
  (score 100, clear of the 91 Queen/Keane class), closing the moved finding
  and item 4 with one mechanism.
- The JF-659 invariant itself (canonical implies kanaOrigin=false) is KEPT
  unchanged: with the kana artist reachable again the invariant's reasoning
  ("the ER match IS the collision evidence") is sound for kana canonicals too,
  on the warm path the finding lives on. Accepted residual (documented on the
  invariant): warm index plus STALE catalog (kana canonical resolved but the
  kana artist deleted) leaves the romanized query in the tier-4 91-tie class
  with the bar inert, the same fuzzy-recovery behavior a Latin canonical
  whose artist is absent already has, not a new class.
- The SearchService.FuzzyMatchPhonetic Fast-mode selector was initially left on
  a => a.Name (codes alone make the kana artist reachable at the 91 floor); the
  /simplify altitude round REFUTED that justification (Fast mode keeps the
  whole tier-1 multi-hit list, so the kana artist ties at 91 with the
  Queen/Keane class and iteration order auto-picks), and the selector is now
  threaded through QueryNameFor like every other warm leg (one line, no-op for
  Latin candidates and the null view). One warm leg deliberately stays raw:
  HandleFuzzyMiss's selector (BaseHandler.HandleFuzzyMiss) is DUAL-USE, scoring
  AND speech (selector(best) is spoken), so threading QueryNameFor there would
  speak 'kuin' instead of the kana name; its reachability cost is near zero (a
  kana artist whose romaji exactly hits the query is a sole tier-1 match and
  never enters the multi-match list). Do not "fix" it into a speech regression.
- The /simplify rounds also landed: ONE derivation gate
  (KatakanaRomanizer.TryRomanize) owning the kana/romanize/emptiness rule for
  every candidate-side site (the artist index load, the song title union, the
  test fakes); the TitleTokens union expressed as Concat+Except (raw-stream
  duplicates preserved, romaji side deduped); WordCoverageCandidates' index
  parameter made REQUIRED (the silent default let the CrossMediaFallback
  JF-440 valve omit it and keep the pre-JF-755 kana degradation; the valve now
  passes its pinned view); and the kana-title re-tokenization across the
  Score/ScorePhonetic stages left as measured-acceptable (1-3ms worst case,
  kana libraries only, on the bounded fallback path inside the 8s budget;
  revisit only if a latency budget ever pins it).
ITEM 5 (normalizer-chain trigger): recorded UNCHANGED. No second script is in
reach: KatakanaRomanizer is kana-only by construction, hi-IN (Devanagari)
and ar-SA (Arabic) native-script values have no romanizer anywhere in the
plugin, and the hi tokenizer fragmentation is documented on KeywordMatcher's
stop-word table as a pre-existing limitation. The chain shape (query-side
script normalizers composed at the search choke points) stays the
prescription for when one lands; nothing built.
<!-- SECTION:NOTES:END -->

## Final Summary

DESIGN DECISION: SYMMETRIC INDEX-SIDE NORMALIZATION (option A). The
kana-canonical-specific bar was rejected on mechanics, not taste: the
'kuin'->Keane pick PASSES the JF-652 bar (real KN code collision), so the flag
flip only adds a wrong-artist prompt, and a hard reject leaves the kana-named
artist unreachable, failing the red proof's second half. The symmetric index
makes the romanized canonical exact-hit the kana artist's romaji key at tier 1
(score 100, clear of the 91 tie), closing the moved JF-658 finding and item 4
with one mechanism; the JF-659 invariant itself is kept (its reasoning is sound
again for kana canonicals on the warm path; the stale-catalog residual is
pinned as the same fuzzy recovery a Latin canonical gets).

MECHANISM: ArtistIndexService builds a Guid->romaji map for kana-containing
names (published with the list and codes in the one JF-448 publish; those
artists' Double Metaphone codes now come from the ROMAJI form, since the raw
kana code is empty and could never collide, i.e. never pass the JF-652 bar);
IArtistIndex.TryGetRomajiName serves it through the pinned views;
ArtistSearch.QueryNameFor is the ONE resolver for the warm artist string legs
(tier 1/2/3 filters, tier 4 and ScoreBestWithCodes selectors, the tier 1.5
tokenization, the Fast-mode pick, the JF-377/JF-420 gate operands via the
matched reading pair); SongNgramIndexService indexes the romanized token
stream alongside the kana one (KeywordMatcher.TitleTokens, the one union);
KeywordMatcher.ScoringTokens (code review) gives the coverage scorers the
romanized reading so an exact kana-title hit scores 105, not the union-diluted
85 that the 90/95 bars refused; the JF-654 song bar's collision leg reads the
romanized title. HandleFuzzyMiss gained a speechSelector seam (scoring through
the key, speech through the display name; the dual-use selector would have
spoken 'kuin'). The warming-gate family is untouched (load-path content only;
WarmingGateCoverageTests green). KatakanaRomanizer gains TryRomanize, the ONE
candidate-side derivation gate (rejects kana-free, blank, and value-identical
romanizations).

RED PROOFS: verified RED on the pre-change tree (throwaway worktree at HEAD,
5 red / 1 control green), green in-tree: the kana canonical against the mixed
library reaches the kana artist at both the SearchAsync and handler layers
(pre-change: the Latin 91-tie artist was queried for songs); the raw-kana and
Latin-romaji queries reach kana-tagged libraries (pre-change: empty); the
romaji keyword finds the kana-titled song through the JF-654 bar
(pre-change: refused at 85, dead collision leg).

ITEM 5: trigger recorded UNCHANGED (no second script in reach; hi/ar have no
romanizer). ITEM 4's album mirror is permanently narrowed (no album index to
carry a key); FILED as JF-773 from the simplify altitude round with the
score-time fix shape.

GATES: /simplify 4 angles (TryRomanize extraction, Concat+Except union,
required index params, cleanups; skips recorded) + /code-review high (6
findings all applied, incl. the song-bar dead leg, the ScoringTokens dilution,
the speech seam, the gate reading pair) + pa:reflect ALIGNED. Suites
5235/5235 both TFMs (baseline 5207, +28, zero broken pins: the zero-pins
prediction held). Release --no-restore -warnaserror 0/0. NO deploy: index
content and matcher layers only, no locale/model/speech surface; the DLL rides
the orchestrator's wave.
