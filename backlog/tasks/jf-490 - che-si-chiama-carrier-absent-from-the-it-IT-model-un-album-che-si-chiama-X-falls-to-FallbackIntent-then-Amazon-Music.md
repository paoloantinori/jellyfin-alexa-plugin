---
id: JF-490
title: >-
  'che si chiama' carrier absent from the it-IT model: 'un album che si chiama
  X' falls to FallbackIntent then Amazon Music
status: To Do
assignee: []
created_date: '2026-09-04 19:07'
labels: []
dependencies: []
references:
  - Device corr=7446fba8 (2026-09-04)
  - JF-469 (the handler-side strip that covers 'che si chiama' once routed)
  - JF-441 (the chiamato-family samples)
  - templates/it-IT.yaml
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-04 device test (corr=7446fba8, 20:55:44): 'un album che si chiama surfer rosa' did not match ANY intent in the it-IT model (AMAZON.FallbackIntent: 'Non ho capito, per favore riprova'), and the bare retry was captured by Amazon Music. The calling-word carrier 'che si chiama' has no sample in the it-IT model's PlayAlbumIntent.

Note: this is a MODEL-LAYER gap, not a handler fix. The it-IT YAML template needs a sample family covering the 'che si chiama' carrier (e.g. 'un album che si chiama {album}', 'un disco che si chiama {album}'), alongside the existing 'chiamato' family added by JF-441. After the template edit + regen, the handler-side JF-469 strip already covers 'che si chiama' (it is in the AlbumCallingWordPrefixes map), so the handler side is ready.

Also check: the 'cerca un album' carrier shape ('cerca un album chiamato {album}') has no sample either (JF-469 documented it as deliberately not covered: 'adding a cerca variant to a play intent is wrong'). Consider whether that decision still holds given the device evidence: the user SAID 'cerca un album chiamato' and expected the skill to handle it. If cerca-carriers are rejected at the model layer, the handler-side recovery never gets a chance.

Acceptance criteria:
- The it-IT template carries 'che si chiama' samples; the regen is surgical.
- Profile-nlu probe: 'un album che si chiama surfer rosa' routes to PlayAlbumIntent with the album slot filled (or at minimum, with the value in a slot the JF-469/JF-new-D recovery can handle).
- The cerca-carrier decision (play vs search intent) is recorded either way.
- Model rebuilt and device-verified.
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
