---
id: JF-916
title: >-
  Catalog h-drop variants for English artist names: 'nora jones' selects nothing
  (the JF-915 residual; extend PhoneticSynonymGenerator, ride the next catalog
  sync)
status: To Do
assignee: []
created_date: '2026-10-20 08:13'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual of JF-915 (the 2026-10-20 Nora Jones wrapper regression fix): the correctly-spoken name now routes perfectly on every form (verified full-pipeline via simulate: 'chiedi a mia collezione di mettere musica di norah jones' delivers PlayArtistSongsIntent musician='norah jones'), but the H-DROPPED spelling 'nora jones' selects NO intent at all (live profile-nlu NO_SELECTION; the pre-fix behavior misselected PlayMoodMusic mood='jones' and produced the 10-artist list, so the fix trades the wrong list for silence on this shape). MECHANISM: the JF-684 catalog selection gate - a catalog-backed slot (JellyfinArtist) whose spoken value matches no catalog value or synonym makes the NLU select no intent; 'nora jones' matches nothing because the catalog's phonetic variant family does not generate English silent-h drops ('norah'->'nora' is an it-IT ASR accent shape, outside the current PhoneticSynonymGenerator rules: velar stops, -ing->-in, consonant doubling). FIX SHAPE: extend the catalog-side synonym generation with an English h-drop rule (drop silent h in 'th'? NO - only word-initial/post-vocalic silent h as in Norah/Sarah-class names; 'th' is a real phoneme) so the next catalog sync mints 'nora jones' on the Norah Jones entry; the change rides PhoneticSynonymGenerator (the JF-379 generative-phonetics umbrella) + its tests, NOT the interaction models. VERIFICATION: profile-nlu 'mettere musica di nora jones' selects PlayArtistSongs after the next catalog sync pins the new version (the JF-684 live-effect path).
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
