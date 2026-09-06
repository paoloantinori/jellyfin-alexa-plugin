---
id: JF-504
title: >-
  Movie-by-title does not route in the model (three phrasings, zero selections;
  handler verified only via simulator): add media-named movie carriers to
  PlayVideoIntent in all locales
status: To Do
assignee: []
created_date: '2026-09-06 08:58'
labels:
  - nlu
  - video
  - movies
dependencies: []
references:
  - device test 2026-09-06
  - profile-nlu evidence in description
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 device session (item 3 of the verification card): 'chiedi a mia collezione di riprodurre ada' (three attempts, error beep) NEVER reached the skill (zero requests in the server logs), and profile-nlu confirms the model cannot route movie-by-title: 'di riprodurre ada' -> no selection (top considered RepeatSingleOnIntent), 'di riprodurre il film ada' -> AMAZON.FallbackIntent, 'riproduci il film ada' -> no selection. The handler path is verified (the simulator delivered slots directly and the launch works, including the static movie URL), so this is purely a MODEL-LAYER gap: PlayVideoIntent's it-IT samples ('Riproduci {title}', 'Metti {title}', 'voglio guardare {title}', ...) are too weak against the now catalog-heavy model (JellyfinArtist/AlbumName/SeriesName catalog types dominate the statistics). Fix shape: add media-named movie carriers to the it-IT template ('{imperative} il film {title}', '{infinitive} il film {title}', '{imperative} il movie {title}' if idiomatic, 'voglio guardare il film {title}', 'cerca il film {title}') plus the equivalents in the 16 other locales' conventions; check the title slot type per locale for consistency (AMAZON.SearchQuery today; a single SearchQuery slot per intent is legal); NLU fixtures for the new shapes in it-IT + en-US; profile-nlu verification that 'di riprodurre il film ada' selects PlayVideoIntent with the title filled; device retest.
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
