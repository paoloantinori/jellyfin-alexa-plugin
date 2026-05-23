---
id: JF-211
title: 'Add "c''è {query}" pattern to SearchMediaIntent after NLU conflict analysis'
status: To Do
assignee: []
created_date: '2026-05-23 17:24'
labels:
  - enhancement
  - interaction-model
  - needs-investigation
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem
"c'è {query}" (is there...) is a very natural Italian pattern: "c'è un film che si chiama matrix?" But it risks NLU conflicts:
- "c'è un brano di Radiohead?" could compete with QueryArtistLibraryIntent / PlayArtistSongsIntent
- "c'è una canzone che..." could compete with PlaySongIntent

## What needs investigation
1. Test whether adding `c'è {query}` to SearchMediaIntent causes NLU misrouting
2. If conflicts exist, consider adding disambiguating concrete samples to competing intents
3. May need the same pattern in other locales (e.g. "is there..." in English)

## Acceptance criteria
- [ ] NLU conflict testing with `c'è` utterances against competing intents
- [ ] If safe: add `c'è {query}` to SearchMediaIntent in it-IT (and equivalents in other locales)
- [ ] If conflicts found: document which utterances conflict and propose mitigation
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
