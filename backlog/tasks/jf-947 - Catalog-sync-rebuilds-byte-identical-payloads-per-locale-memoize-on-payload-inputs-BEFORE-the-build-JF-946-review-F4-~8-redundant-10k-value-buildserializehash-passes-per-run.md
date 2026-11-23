---
id: JF-947
title: >-
  Catalog sync rebuilds byte-identical payloads per locale: memoize on payload
  inputs BEFORE the build (JF-946 review F4, ~8 redundant 10k-value
  build+serialize+hash passes per run)
status: To Do
assignee: []
created_date: '2026-11-22 20:15'
labels:
  - catalog
  - efficiency
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/LibrarySyncService.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Efficiency review finding F4 from JF-946 (verified against Jellyfin v10.11.8/v12.4 sources by the reviewer): the catalog sync rebuilds AND re-serializes each type's payload for every synced locale even when the payload is byte-identical across locales, because the JF-717 memoization keys on the hash of the SERIALIZED bytes (computed after the build). With the Song catalog (~10k values, the largest set by far) the 16 locales collapse to 8 distinct payload classes (it, de, es x3, fr x2, pt, ja, nl, empty-generator x6): 8 legs do a full FromItems pass (~30-100ms), a JsonSerializer.Serialize of ~1.5-4MB (~30-120ms), a UTF8 copy, and a SHA256, only to skip the upload. Total waste ~0.5-2s CPU and ~40-80MB transient allocation per weekly run. Fix shape: check the memo BEFORE the build, keyed on the payload INPUTS ({type}:{catalogId}:{LocalePrefix.Of(locale)}:{itemTuplesHash}), with itemTuplesHash computed once per type per run (the items list is the same instance across locale legs per the JF-727 sourcing loop); types whose seed content varies by locale (only Audiobook's per-locale generic word) need that term in the key or keep the byte-hash path. The pattern predates JF-946 for the other five types (smaller sets); the song set made it visible.
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
