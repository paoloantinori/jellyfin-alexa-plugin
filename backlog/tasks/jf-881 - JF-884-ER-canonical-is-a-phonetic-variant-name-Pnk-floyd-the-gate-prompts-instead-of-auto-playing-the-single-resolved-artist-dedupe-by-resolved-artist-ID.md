---
id: JF-881
title: >-
  JF-884: ER canonical is a phonetic variant name ("P!nk floyd") - the gate
  prompts instead of auto-playing the single resolved artist; dedupe by resolved
  artist ID
status: Done
assignee: []
created_date: '2026-10-13 13:58'
updated_date: '2026-10-13 15:34'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
REGRESSION from the 2026-10-12 forced catalog sync (backdated LastCatalogSync re-minted the catalog versions). Live evidence 2026-10-13: e2e "metti una canzone dei pink floyd" (it-IT) now fails with a DISAMBIGUATION ask response (APL RenderDocument + UpdateDynamicEntities, no AudioPlayer.Play); profile-nlu shows the musician slot canonical = "P!nk floyd" (a phonetic-synonym string promoted by Amazon ER to the matched VALUE's canonical name), while the library has exactly ONE floyd artist: "Pink Floyd" (id 7d23a278, verified via /Artists?SearchTerm=floyd). The handler receives the variant canonical, the artist search resolves Pink Floyd at high score, but the JF-420.1 exact-name-equality bypass fails ("P!nk floyd" != "Pink Floyd") and the JF-690/JF-420 gate fires the yes/no prompt because its distinct-artist count is computed on NAME-resolved matches (>= 2 surface candidates). It also speaks the variant name, which reads wrong.

ROOT: the JF-690 gate's distinct-count is name-based; it must be ID-based. When every ER canonical's resolved library artist collapses to the SAME id, distinct = 1 and the gate must auto-play the winner (the JF-690 design says ">= 2 DISTINCT library artists" fires the prompt; the dedupe is the missing half). Same for the JF-420.1 bypass: compare against the RESOLVED artist, not the canonical string.

GUARDS: a genuine two-artist case ("pink" -> P!nk AND Pink Floyd, the JF-684 evidence) must STILL prompt (two distinct ids); the Pearl Jam collaboration case (Pearl Jam, Pearl Jam w, Mad Season & Pearl Jam = DISTINCT library artists) must still prompt; a variant-only collapse auto-plays the one artist. Unit tests on MultiValueErDisambiguation with the JF-684 fixture shape.

Priority HIGH: this is a live regression on a core music ask (every variant-canonical artist ask now prompts instead of playing), introduced by the normal catalog re-mint, i.e. any user whose catalog gets re-synced is exposed.
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
EXECUTED on branch jf884 (6f88e895), merged to main 6f8eda61, pushed, DEPLOYED and LIVE-VERIFIED on minix (Simulator with the variant canonical "P!nk floyd" now answers AudioPlayer.Play, no ask). ArtistSearch.ResolveErCandidates is the ONE resolver feeding the gate (exact-name layer, then fuzzy at the ErVariantResolutionThreshold=80 judgment bar, dedupe by RESOLVED ARTIST ID, no Double Metaphone requirement because "P!nk floyd" vs "Pink Floyd" encode differently - a code bar would reject the live shape; containment pairs pass the JF-381 band). TryArbitrate runs on single-value ER slots, auto-plays on collapse-to-one-id, keeps the JF-420.2 ask on >= 2 distinct ids; PlayArtistSongs stands the JF-377/420/382 gates down for an adopted survivor and serves the RESOLVED artist's real name. 8 guards incl. the regression pin, the two-distinct-ids ask, the Pearl Jam collaboration ask, and the red-verified load-bearing conjunct. Gates: /simplify (4 applied, 2 reasoned skips) + /code-review high (6/6 addressed). Tests 5936/5936 BOTH TFMs twice; build 0 warnings. The e2e pink-floyd fixtures should go green on the next suite run.
<!-- SECTION:FINAL_SUMMARY:END -->
