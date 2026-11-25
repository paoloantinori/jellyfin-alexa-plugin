---
id: JF-964
title: >-
  en-US conjunction probe: the naked 'play {#}' on 4 intents (2 catalog-slotted)
  is the JF-961 configuration; gate before any en alignment
status: To Do
assignee: []
created_date: '2026-11-25 14:55'
labels:
  - nlu
  - catalog-gate
  - probe
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fable's JF-963 review point 5 (2026-11-25): en-US carries the naked 'play {#}' carrier on FOUR intents, two of which consume catalog-backed slots (PlayArtistSongsIntent via JellyfinArtist on the musician slot; PlayBookIntent via AudiobookTitle) — the exact wiring configuration the JF-961 ablation proved produces the conjunction Fallback on es-US (artist wiring + at least one more catalog wiring breaks reproduce-shaped selection on non-famous titles). Before ANY en-US alignment rows land (JF-965 and later batches), probe live whether en-US already has the disease: (1) 'play the song <non-famous title>' x2 probes (pick a real library song title that is not famous), (2) 'play <non-famous title>' x2 (the naked form), (3) verb-variant control ('listen to <same title>' x1), (4) evergreen control ('play songs by pink floyd' x1). Reading per the ablation's matrix: Fallback on the carrier form + green on the verb variant = the conjunction is live in en-US too (file the finding; alignment rows in en-US then need the same conjunction-awareness as es); all green = en-US is currently healthy (the en sample density may be absorbing it) and alignment can proceed with the standard graph-diff sequence only. Cheap: ~6 profile-nlu calls.
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
