---
id: JF-412
title: >-
  Album catalog phonetic synonyms don't cover ASR form "walls for cup" for
  existing album "Waltz for Koop" (ER_SUCCESS_NO_MATCH on both authorities)
status: To Do
assignee: []
created_date: '2026-08-28 15:38'
updated_date: '2026-08-28 16:15'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-28 15:54:21: slot album="walls for cup" (ASR render of "Waltz for Koop") got ER_SUCCESS_NO_MATCH from BOTH the static AlbumName catalog authority and the dynamic (echo-sdk.dynamic) authority. The library DOES contain album "Waltz for Koop" by Koop (verified via Jellyfin API). The catalog/phonetic-synonym architecture (JF-96.2 catalog sync + JF-362 Romance phonetic synonyms; artist-sideDouble Metaphone already covers Koop->cup, both code KP, see BaseHandler FuzzyMatchPhonetic) apparently does not produce album-name synonyms that cover "walls for cup" (or "waltz"->"walls" ASR drift), so entity resolution could not help and the request fell through to the defective fuzzy fallback (tracked separately).

Investigate: how AlbumName catalog values + synonyms are generated (LibrarySyncService/CatalogManager upload path), whether album names get the same phonetic synonym treatment as artist names, and whether the per-name variant cap (5) squeezes out the needed variants.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Root cause identified: which layer failed to cover the form (synonym generation rules for album names vs artist names; 'waltz'->'walls' coverage; per-name variant cap)
- [ ] #2 Catalog payload for the affected library demonstrably contains a synonym (or slot value) that entity-resolves 'walls for cup' style ASR output for 'Waltz for Koop' (verifiable via SMAPI catalog inspection or the CatalogController payload)
- [ ] #3 If the gap is a generator-rule miss, extend Phonetics generators with the rule and add unit tests; if it is the variant cap, document why and adjust
- [ ] #4 Do NOT swap AlbumName to an AMAZON built-in type (anti-pattern #10); the fix must stay in the catalog/phonetic architecture
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
INVESTIGATION RESULT (AC #1): the gap is STRUCTURAL, not a rule miss. Empirical probe (scratch test running PhoneticSynonymGenerator.GenerateSynonyms("Waltz for Koop", "it-IT")): variants = ['Ualtz for Koop', 'Valtz for Kop', 'i Ualtz for Koop']. The ASR form 'walls for cup' is unreachable: the generators model L1 pronunciation variants of the English name (w->u/v, oo->o, consonant doubling), while the incident shape is ASR transcription drift ('waltz'->'walls' via /t/-cluster loss, 'koop'->'cup' via vowel quality + k/c). No rule set of the first kind can enumerate the second kind exhaustively.

RECOMMENDED FIX DIRECTION (not implemented this session, needs its own change): album-side phonetic matching in the PlayAlbum fuzzy fallback, mirroring the song pipeline (ScoreWithPhoneticFallback / Double Metaphone): 'cup' and 'Koop' both code KP, 'waltz'/'walls' share the vowel+final-s skeleton. The JF-336 comment already anticipates this ('true phonetic matching would need a precomputed album index, cf. ArtistIndexService'). A bounded album-set phonetic rescore would be smaller than a full index. Extending the synonym generators with ASR-drift rules was evaluated and rejected: whack-a-mole, violates the coverage-vs-precision architecture by chasing transcription noise.

With JF-408's length floor deployed, the catastrophic outcome of this gap (auto-playing 'O') is already mitigated: the miss now falls through to a clean not-found instead of a wrong play. The catalog gap therefore only costs recall (Koop album not reachable by voice in that ASR shape), no longer correctness.
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
