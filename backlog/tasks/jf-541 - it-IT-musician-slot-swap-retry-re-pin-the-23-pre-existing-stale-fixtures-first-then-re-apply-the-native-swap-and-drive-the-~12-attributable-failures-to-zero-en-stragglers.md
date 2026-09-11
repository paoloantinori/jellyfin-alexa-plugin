---
id: JF-541
title: >-
  it-IT musician-slot swap retry: re-pin the 23 pre-existing stale fixtures
  first, then re-apply the native swap and drive the ~12 attributable failures
  to zero (+ en stragglers)
status: To Do
assignee: []
created_date: '2026-09-11 02:03'
labels:
  - nlu
  - interaction-model
  - it-IT
  - jf415-followup
  - fixtures
dependencies: []
references:
  - JF-415
  - JF-508
  - JF-523
  - JF-490
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from JF-415's it-IT extension rollback (2026-09-11 ~04:00, full evidence in JF-415's notes). NUMBERS: swapped it-IT model = 35 NLU-suite failures; pre-swap baseline (rolled-back deployed model, run the same night) = 23 failures, WITH THE SAME CLUSTER (Riproduci album thriller, Suona musica dei queen, suona matrix, disco thriller shapes, playlist casuale...). So ~12 failures are ATTRIBUTABLE to the native swap and ~23 are PRE-EXISTING stale fixtures (they fail identically without the swap; last verified green against an older catalog/model state, likely drifted by the weekly catalog syncs since).

STATE NOW: it-IT deployed model ROLLED BACK to pre-swap committed + catalog injection (which still re-types musician slots to JellyfinArtist via the injection path - so the deployed model DOES declare JellyfinArtist, provenance differs, and the runtime C# resolver matches both states). The COMMITTED repo model stays swapped (JF-415 merge): repo and deploy agree on the declared type, they differ on provenance (native block vs injection). The en-* swap is deployed and stays (en-GB fully green; the canonicalization fix verified).

SCOPE WHEN TAKEN: (1) re-pin the ~23 pre-existing stale it-IT fixtures FIRST (probe each, update expected winners or fix the model where a regression is unacceptable - 'Riproduci album thriller' routing to NO INTENT is the headline case to FIX, not re-pin); (2) then re-apply the native it-IT swap (redeploy the committed model + catalog rebind) and drive the ~12 attributable failures to zero via sample-level competition tuning (the JF-508/JF-490 toolkit: carrier words, competing-sample trimming); (3) the scattered en stragglers from the same night (e2e-class excluded per the documented en unreliability): en-US 'Repeat the song' (no intent), 'Play the song hotel california from the eagles' (song swallows the phrase, musician unfilled), 'Play imagine by john lennon next' (song='"Imagine" by John Lennon' with literal quotes), en-CA 'Play a random movie' (genre='movie' instead of media_type), en-IN 'play stranger things season two episode one' - probe, classify re-pin vs fix. NOTE for whoever runs the suite: the runner's own e2e tests inside the NLU script need the live endpoint and fail transients during model rebuilds; the 409-on-simulate class means another SMAPI consumer is active.
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
