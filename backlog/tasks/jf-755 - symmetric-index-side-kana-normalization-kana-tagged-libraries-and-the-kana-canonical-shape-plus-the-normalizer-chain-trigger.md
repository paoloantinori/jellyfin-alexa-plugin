---
id: JF-755
title: >-
  JF-755 - symmetric index-side kana normalization (kana-tagged libraries and the
  kana-canonical shape) plus the normalizer-chain trigger for a second script
status: To Do
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
<!-- SECTION:NOTES:END -->
