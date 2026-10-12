---
id: JF-873
title: >-
  JF-874: catalog-backed MovieTitle slot type - the architectural fix for video
  one-shot wrappers (SearchQuery span proven wrapper-hostile)
status: Done
assignee: []
created_date: '2026-10-12 18:37'
updated_date: '2026-10-12 21:13'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Architectural fix surfaced by JF-869's A/B proof (scratch skill 65da1ba5, two identical rebuilds): it-IT VIDEO one-shot wrappers ("chiedi a mia collezione di mettere/riprodurre/vedere/guardare il film X") are NOT row-fixable while PlayVideoIntent.title is AMAZON.SearchQuery - the SearchQuery span is wrapper-hostile in NLU recall. Proven: full row density (noun-carrying bare infinitives, noun-less, imperative) does NOT flip any wrapper; swapping title to AMAZON.MusicRecording flips all four wrappers green but collapses every bare imperative control to PlayByGenreIntent (disqualified on the same two-build discipline); every wrapper family that DOES work targets a non-SearchQuery slot (MusicRecording, JellyfinArtist, SeriesName, EpisodePosition, AudiobookTitle, word-form NUMBER). Fix: a catalog-backed MovieTitle custom type in the JF-96.2 family (CatalogSlotTypes/CatalogSlotTypeNames + sync from Movie items + phonetic synonyms + the JF-684 selection semantics), starting with it-IT where the evidence lives; en-US spot-probe confirms the same disease with a different symptom ("ask jellyfin player to play the movie sunny dancer" swallowed by PlaySong). Sibling residual on the same recall axis: noun-less "di leggere option b" still Fallback in the profiler (noun-carrying form fixed in JF-869); per JF-504 device-vs-profiler divergence it may work on device - verify after the next deploy before designing anything. Also verify the catalog-change live-effect path (JF-684: changes go live only when the next catalog sync pins the new version).
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf874 (74cad87e), merged to main a15adac1 (mirror-conflict resolved by regeneration, freshness verified), pushed. DESIGN VERDICT (scratch A/B with a real seed-only movie catalog minted on the vendor account, "JF874 MovieProbe" amzn1.ask.interactionModel.catalog.22a7431a, hosting gist deleted, catalog left for future A/B): variant A full swap disqualified (wrappers green but brano/musica controls collapse to PlayByGenre, two identical rebuilds); variant C dual-slot rejected by SMAPI full build; SHIPPED variant B2, the additive PlayMovieIntent with the catalog-typed movie slot and exactly four Di-form wrapper-tail rows, PlayVideoIntent byte-identical. All four wrappers route PlayMovieIntent ER-matched; bare imperatives/infinitives, non-catalog titles (raw-filled via the anchored carrier: the JF-684 selection gate does NOT hard-block lexically anchored carriers, so post-sync additions still reach the handler) and controls green, stable across two identical rebuilds. Landed: CatalogType.Movie through the JF-96.2 family (names parity pin, wiring fifth row, MovieCount, User.MovieCatalogId, seed enrichment, positional-arity edge), PartialNameSynonyms widened to Movie (JF-508 album caution NOT extended, documented), PlayVideoIntentHandler serves both intents (canonical-first per JF-659, empty-slot elicitation delegates to the PlayVideo dialog entry), it-IT template MovieTitle 12-title seed + PlayMovieIntent, fixtures + wrapper-battery pinned, mirrors regenerated. Gates: /pa:reflect ALIGNED + /simplify (3 applied) + /code-review high (2 fixes, 3 documented acceptances). Tests 5821/5821 BOTH TFMs; Release 0/0; validator PASS 305 warnings = 289 baseline + 16 known-class rows (no new class). Residuals FILED: JF-876 (brano/movie collision steal), JF-877 (movie multi-value ER autoplay); documented in code: the duplicate Di-form trainer-flip shape reads benign (film still plays, ER canonical drops). Deploy needs DLL + it-IT model rebuild + a real catalog sync to pin the MovieTitle version (pre-deploy red window on the new fixture rows is expected).
<!-- SECTION:FINAL_SUMMARY:END -->
