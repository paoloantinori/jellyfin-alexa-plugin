---
id: JF-415
title: >-
  Swap musician slot from AMAZON.Musician to catalog-backed JellyfinArtist on
  en-* locales (fixes knowledge-graph entity canonicalization)
status: To Do
assignee: []
created_date: '2026-08-30 06:08'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Platform finding (2026-08-29, probe-evidenced, research report 2026-08-30): the AMAZON.Musician built-in slot type on en-* locales (en-US/GB/AU/CA/IN) replaces the slot value with the canonical knowledge-graph entity name instead of the spoken text. Probes: queen->'Paula Abdul', the beatles->'John Lennon', coldplay->'Christopher Anthony John Martin', pink floyd->'Syd Barrett'. This contradicts Amazon's own documentation (Nov 2023) which shows slot.value containing the raw spoken text. Non-en locales (it/fr/de verified) preserve the raw text. Handler consequence: artist search on the mangled canonical name -> clean not-found (no wrong plays; the not-found-first design holds). The feature 'play an album by X' is degraded to not-found on all en-* locales.

Mitigation: swap the musician slot to the catalog-backed JellyfinArtist custom type (JF-96.2 architecture). Custom slot types return raw spoken text. The swap must be atomic per locale (anti-pattern #4: same slot name = same type across all intents in a locale). The catalog must be verified as populated for en-* before the swap.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Swap the musician slot from AMAZON.Musician to JellyfinArtist custom slot type in ALL intents that use it, in the 5 en-* locales (en-US, en-GB, en-AU, en-CA, en-IN): PlaySongIntent, PlayAlbumIntent, PlayArtistSongsIntent, FindSongByArtistIntent, QueryArtistLibraryIntent, and any other intent declaring a musician slot (verify with a script)
- [ ] #2 Slot type consistency (anti-pattern #4): the musician slot must use the SAME type across ALL intents within each locale - the swap must be atomic per locale, not per intent
- [ ] #3 The JellyfinArtist catalog must be verified as populated for the en-* locales BEFORE the model swap (CatalogSyncLocales config or catalog inspection via SMAPI)
- [ ] #4 Non-en-* locales are NOT swapped in this task (they don't exhibit the canonicalization bug; swapping them would be scope creep - evaluate separately after the en-* swap is verified)
- [ ] #5 Post-swap verification: profile-nlu probe on en-GB confirming 'an album by queen' returns musician=queen (raw), not a canonical entity name
- [ ] #6 Full NLU suite green on the swapped locales
- [ ] #7 Research report referenced: claudedocs/research_amazon_musician_entity_canonicalization_2026-08-30.md
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
