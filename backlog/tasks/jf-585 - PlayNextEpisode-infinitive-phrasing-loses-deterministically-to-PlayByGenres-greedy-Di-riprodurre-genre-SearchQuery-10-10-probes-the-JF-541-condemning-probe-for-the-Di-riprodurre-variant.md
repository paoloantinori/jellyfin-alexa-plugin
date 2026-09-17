---
id: JF-585
title: >-
  PlayNextEpisode infinitive phrasing loses deterministically to PlayByGenre's
  greedy 'Di riprodurre {genre}' SearchQuery (10/10 probes; the JF-541
  condemning probe for the Di-riprodurre variant)
status: To Do
assignee: []
created_date: '2026-09-17 18:40'
labels:
  - nlu
  - competition
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-583 closure probes (2026-09-17, live profile-nlu it-IT): 'Di riprodurre l'ultimo episodio di stranger things' routes to PlayByGenreIntent deterministically (10/10 probes across 2 identical-content model rebuilds) because PlayByGenreIntent carries the greedy 'Di riprodurre {genre}' sample (genre = AMAZON.SearchQuery) whose span swallows the whole tail. The {infinitive} {episode_position} episodio di {series_name} sample exists and is correct training data; an explicit literal-carrier sample (Di riprodurre l'ultimo episodio di {series_name}, the JF-504 precedent, landed in 532159bc) did NOT flip the outcome. The imperative and Metti families WIN (their sibling Metti {genre} exists but loses), so the competition is specific to the Di-riprodurre prefix. The failing it-IT fixture row (test_nlu.py ... ultimo episodio di stranger things, expecting PlayNextEpisodeIntent + episode_position) is RED until this lands; the row documents the desired behavior. Candidate resolutions, to be probed before choosing: (a) REMOVE 'Di riprodurre {genre}' from PlayByGenreIntent (subtract before add): the template comment documents it was deliberately kept after JF-541 with handler-side genre recovery, and pins the two fixture shapes that flip on removal ('disco thriller' and the 'musica dei' shapes) - probe what those route to after removal (FallbackIntent? no-selection?) and whether their handler recovery still applies; (b) weaken only the Di-riprodurre variant of the greedy family (keep Riproduci/Suona/Metti/Pleia {genre}); (c) accept the capture and note that the latest-episode semantics are NOT recoverable handler-side from a genre capture (the recovery argument that justified keeping it covers genre/artist shapes, not the position family). Evidence class: the JF-541 probe discipline (remove only when a probe condemns) - this IS the condemning probe for the Di-riprodurre variant. Related: JF-583 (the slot), JF-354 (Mood architecture), the it-IT template comment at the PlayByGenreIntent block documenting the original keep decision.
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
