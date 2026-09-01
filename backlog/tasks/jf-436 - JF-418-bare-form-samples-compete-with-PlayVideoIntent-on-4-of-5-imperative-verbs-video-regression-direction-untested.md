---
id: JF-436
title: >-
  JF-418 bare-form samples compete with PlayVideoIntent on 4 of 5 imperative
  verbs: video-regression direction untested
status: To Do
assignee: []
created_date: '2026-09-01 10:52'
labels:
  - code-review
  - nlu
  - it-IT
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml:873'
  - tests/integration/fixtures/it-IT.yaml
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Untracked finding from the 2026-09-01 JF-420.3 code-review (review round 2, filed per the review-recommendation discipline): JF-418's bare '{imperative} {musician}' samples (templates/it-IT.yaml:873) collide in surface shape with PlayVideoIntent's '{imperative} {title}' (4 of 5 imperative verbs identical) and PlayByGenreIntent's '{imperative} {genre}'. The template's own comment records that such bare forms previously fell through to PlayVideoIntent; AMAZON.Musician is demonstrably loose (probe resolved 'suona pink floyd' to musician='P!nk floyd'), so band/movie names can now flip to PlayArtistSongsIntent and answer with artist songs or an artist not-found instead of the movie. The existing guard fixture ('Riproduci star wars') covers only ONE verb; the JF-418 probes tested artist queries only.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 NLU fixtures cover the video-regression direction on the NEW imperative verbs: bare movie/series titles with 'Suona'/'Metti'/'Pleia' (e.g. 'suona matrix', 'metti pulp fiction') assert PlayVideoIntent, not PlayArtistSongsIntent
- [ ] #2 If any fixture fails, add disambiguating samples to the it-IT template (carrier words for the artist intent or vice versa) and regenerate; if all pass, record the green evidence in the task and close
- [ ] #3 Re-run the full it-IT NLU fixture suite after any template change
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
