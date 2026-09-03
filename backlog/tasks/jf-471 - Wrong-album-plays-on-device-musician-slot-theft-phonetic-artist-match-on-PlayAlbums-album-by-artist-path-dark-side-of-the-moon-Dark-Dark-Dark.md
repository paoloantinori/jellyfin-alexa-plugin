---
id: JF-471
title: >-
  Wrong album plays on device: musician-slot theft + phonetic artist match on
  PlayAlbum's album-by-artist path (dark side of the moon -> Dark Dark Dark)
status: Done
assignee: []
created_date: '2026-09-03 15:39'
updated_date: '2026-09-03 16:48'
labels: []
dependencies: []
references:
  - 'Device session logs corr=38498471 (2026-09-03 17:30)'
  - 'JF-469 (the slot-theft half, unfixable at model)'
  - JF-420 fair-comparison gate precedent
  - JF-363 Confirm band
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's on-device test session (2026-09-03 ~17:30, logs corr=38498471): "riproduci album dark side of the moon" (in-skill session) selected PlayAlbumIntent with slots album=EMPTY, musician='dark side of the moon' (the JF-469 Amazon entity theft, now confirmed with real ASR on device). The album-by-artist path then ran ArtistSearch('dark side of the moon'), the phonetic chain matched the WRONG artist 'Dark Dark Dark' (present in the live library), and the skill played its album 'In Your Dreams' (APL card evidence in the logs). User experience: asked for Dark Side of the Moon, got an unrelated album silently.

Two-layer chain, one fixable layer:
1. Amazon theft (musician slot eats the album span): NOT fixable at the model layer (JF-469 evidence; the sample is verbatim-present). Handler-side value normalization (JF-469's leading 'chiamato' strip) does not apply here (no bleed, full theft).
2. PLUGIN-FIXABLE: the album-by-artist path accepts a phonetic artist match for a query that is clearly NOT an artist name and then silently plays a DIFFERENT artist's album. Candidate guards (pick by evidence): (a) when the PlayAlbum musician-path's artist match is phonetic-only (Double Metaphone code collision, not a name/fuzzy-text match) AND the query carries album-shaped statistics (multi-word title-case span), require the JF-363 Confirm band instead of auto-play; (b) mirror the JF-420 containment-vs-full-name fair-comparison gate on this path ('dark side of the moon' vs 'Dark Dark Dark' should lose or prompt); (c) at minimum, announce which artist matched (it may already via FoundAlbumInstead? verify: the response was 'In riproduzione' with no substitution announcement per the logs, so the announcement gate did NOT fire on this path).

Acceptance criteria:
- Reproduce at unit level: PlayAlbumIntent musician='dark side of the moon', library contains 'Dark Dark Dark' -> today auto-plays 'In Your Dreams'; after the fix it either prompts (Confirm) or not-founds cleanly.
- The legit album-by-artist flow ('riproduci l'album dei pink floyd') stays byte-identical.
- Device re-verification obligation recorded for Paolo.
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
Implemented, deployed, and live-verified (commit 3fb45c4a).

Mechanism probe REFUTED the task's phonetic hypothesis with a pinned pre-fix test: the winner was the JF-437 word-coverage tier (the only unbarred tier), 'Dark Dark Dark' tokenizing to 'dark' as a subset of the query's {dark, side, moon}, honest fuzzy score 42, NO Double Metaphone collision (TRKS vs TRKT). Fix: BaseHandler.PassesArtistMatchAcceptance, a decision-point gate that re-scores the chain's match through the matcher itself (same overload, threshold source, and phonetic lookup the tiers use, over the same pinned index snapshot, JF-448 pattern): below the acceptance bar the album-by-artist path refuses with the clean NotFoundAlbumByArtist. Every legit class pinned auto-playing unchanged (containment/exact, ASR truncation, qualifier queries, the JF-381 phonetic flagship cup->Koop); the album-title-present path pinned byte-identical (scope test).

Live verification on minix post-deploy: simulator PlayAlbumIntent musician='dark side of the moon' now answers 'Spiacente, non ho trovato nessun album dell'artista dark side of the moon' (the device bug closed); the pink floyd control still auto-plays. Suite 3101/3101 (6 tests, both-arm mutation-verified), Release 0 warnings, validators baseline.

Review: pr-review-toolkit:code-reviewer, zero P1/P2; two P3@85 fidelity corners documented in the helper doc (user threshold above 90 refusing containment per JF-363 semantics; disabled-index fail-open divergence). Residual gap filed as JF-473 (single-word containment parity with the JF-377 downgrade on PlayArtistSongs). Device re-verification item for Paolo: 'riproduci album dark side of the moon' must not-found cleanly; 'riproduci l'album dei pink floyd' unchanged.
<!-- SECTION:FINAL_SUMMARY:END -->
