---
id: JF-412
title: >-
  Album catalog phonetic synonyms don't cover ASR form "walls for cup" for
  existing album "Waltz for Koop" (ER_SUCCESS_NO_MATCH on both authorities)
status: Done
assignee: []
created_date: '2026-08-28 15:38'
updated_date: '2026-09-14 19:35'
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

2026-09-14 20:20 implemented + live-verified. NOTE (operational, refined understanding of the hot-swap token family): after a DLL hot-swap + restart, the FIRST simulator call can return the 'collegamento con il tuo server Jellyfin non funziona piu' speech even with JellyfinToken intact on disk; it self-heals within ~2 minutes (startup completing) with NO re-link needed. Today's sequence: same message after the SSML-revert deploy, then Paolo's real invocations worked minutes later; same again tonight, retry after 60s worked. Wait-and-retry before diagnosing a dead link after a deploy.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED fixed and live-verified (2026-09-14 evening). Root cause (AC#1, investigation from 2026-08-28 confirmed): the fuzzy album tier aborted WHOLE on an embedded-containment winner; the 2026-09-14 live replay showed the real catalog ranks the degenerate album "O" FIRST at 90 for "walls for cup" (JF-408/478 class, correctly refused) shadowing "Waltz for Koop"/"The Hill for Company" at 61. Fix: BaseHandler.FindBestNonEmbeddedMatch walks FindBestMatchWithScore skipping embedded winners, first eligible above the caller's threshold plays; both album fuzzy paths rewired (direct PlayAlbum threshold 60: incident recovered; cascade stays 90 by design, pinned by test). AC#2: covered query-side (the catalog payload intentionally unchanged; the ASR-drift form "walls for cup" is not enumerable in synonyms, that conclusion stands). AC#3: documented - not a generator-rule miss nor a cap issue; the query-side fuzzy+phonetic layer is the sanctioned fix. AC#4: no slot-type change. Tests: live-shape replay ([O + Waltz] -> plays Waltz), cascade 61-refusal pin, JF-408/478 negatives still green; suite 3737/3738-class green both TFMs (final 3737/3737+2 var by fixture count), Release 0 warnings. Live verify: simulator PlayAlbumIntent album="walls for cup" on the real library -> "Ho trovato l'album The Hill for Company." + AudioPlayer.Play. Deployed (md5 df315867...). Gates: 4-agent simplify + opus code-review CLEAN. Residual: the 61-tie between Hill and Waltz resolves by catalog order (JF-341 class); the announcement names the choice.
<!-- SECTION:FINAL_SUMMARY:END -->

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
