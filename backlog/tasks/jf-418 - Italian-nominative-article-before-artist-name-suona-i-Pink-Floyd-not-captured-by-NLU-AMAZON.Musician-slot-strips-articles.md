---
id: JF-418
title: >-
  Italian nominative article before artist name ('suona i Pink Floyd') not
  captured by NLU - AMAZON.Musician slot strips articles
status: To Do
assignee: []
created_date: '2026-08-31 05:59'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live finding 2026-08-30 (on-device + profile-nlu): all Italian imperative forms with the nominative article before the artist name fail to route to PlayArtistSongsIntent. The NLU returns None for 'suona i queen', 'suona i beatles', 'suona i radiohead', 'suona i nirvana', 'riproduci i queen'. The bare form without article ('suona queen', 'suona pink floyd') works correctly (JF-418 fix).

Root cause hypothesis: the AMAZON.Musician slot type strips or rejects leading Italian articles when filling the slot from a bare '{imperative} {musician}' sample. The NLU sees 'suona i queen' and tries to match 'i queen' to the {musician} slot, but the article 'i' prevents a clean match. Without the article, 'queen' fills the slot correctly.

This is a natural Italian speech pattern: referring to bands with the definite article ('i Pink Floyd', 'i Queen', 'gli Radiohead') is more common than the bare form in everyday Italian. Not being able to use it is a significant UX gap for Italian users.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Investigate whether adding nominative article vocabulary (il/lo/la/i/gli/le) to the PlayArtistSongsIntent template generates samples that make the NLU fill the {musician} slot with 'i queen' instead of rejecting the article
- [ ] #2 If vocabulary expansion works: add nominative_article vocabulary + templates '{imperative} {nominative_article} {musician}' and regenerate
- [ ] #3 If vocabulary expansion does NOT work (the NLU strips articles from AMAZON.Musician regardless): investigate whether the article can be captured as part of the slot value via AMAZON.SearchQuery or a custom slot type
- [ ] #4 Probe-verify: 'suona i queen' → PlayArtistSongsIntent with musician='i queen' or musician='queen' (article stripped)
- [ ] #5 The fix must not break existing bare imperative routing: 'suona queen' must continue to work
- [ ] #6 Check whether the same article issue exists in other Romance locales (fr 'le', es 'el/los/las', pt 'o/a/os/as'])
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
