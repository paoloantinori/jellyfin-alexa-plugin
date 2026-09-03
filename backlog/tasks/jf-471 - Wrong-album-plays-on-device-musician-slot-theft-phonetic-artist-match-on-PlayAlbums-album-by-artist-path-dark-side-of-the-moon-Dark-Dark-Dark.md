---
id: JF-471
title: >-
  Wrong album plays on device: musician-slot theft + phonetic artist match on
  PlayAlbum's album-by-artist path (dark side of the moon -> Dark Dark Dark)
status: In Progress
assignee: []
created_date: '2026-09-03 15:39'
updated_date: '2026-09-03 15:51'
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
