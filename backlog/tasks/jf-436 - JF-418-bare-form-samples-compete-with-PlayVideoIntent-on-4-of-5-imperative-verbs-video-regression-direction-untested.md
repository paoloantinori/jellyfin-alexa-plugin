---
id: JF-436
title: >-
  JF-418 bare-form samples compete with PlayVideoIntent on 4 of 5 imperative
  verbs: video-regression direction untested
status: Done
assignee:
  - zai
created_date: '2026-09-01 10:52'
updated_date: '2026-09-01 21:10'
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
- [x] #1 NLU fixtures cover the video-regression direction on the NEW imperative verbs: bare movie/series titles with 'Suona'/'Metti'/'Pleia' (e.g. 'suona matrix', 'metti pulp fiction') assert PlayVideoIntent, not PlayArtistSongsIntent
- [x] #2 If any fixture fails, add disambiguating samples to the it-IT template (carrier words for the artist intent or vice versa) and regenerate; if all pass, record the green evidence in the task and close
- [x] #3 Re-run the full it-IT NLU fixture suite after any template change
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-436: the video-regression direction is now MEASURED, PINNED, and documented - it is real and total.

THE FINDING (the task's purpose, answered): ZERO of 14 probed bare movie/series titles route to PlayVideoIntent on the new imperative verbs. 'suona/metti/pleia matrix' and 'pulp fiction' -> PlayArtistSongsIntent musician slot (AMAZON.Musician accepts them); famous multi-word titles ('star wars') -> PlaySongIntent song slot (the pre-JF-418 documented class where the handler resolves content type); series titles ('breaking bad', 'stranger things') -> no selectedIntent with PlayEpisodeIntent pulled in via SeriesName catalog resolution; 'suona inception' -> PlayRadioIntent. The PlayVideoIntent 'Suona/Metti/Pleia {title}' samples are effectively dead surface on these verbs.

WHAT LANDED (fixtures only, no model changes): 4 NLU fixtures pin the ACTUAL routing (the do-not-force rule, JF-439 precedent): suona matrix + pleia matrix + metti pulp fiction -> PlayArtistSongsIntent/musician; suona star wars -> PlaySongIntent/song. All 4 pass in the full it-IT suite (147 tests: 144 passed, 3 failed - none of ours).

ORCHESTRATOR DECISION (conservative, model untouched): the fix branch of AC#2 (add disambiguating samples to restore PlayVideoIntent) was NOT taken overnight - the JF-438 lesson is that it-IT model changes have wide blast radius (the RepeatSingle removal flipped 'suona la canzone sugar free jazz'), and restoring video routing would compete with the just-decided JF-418 bare-form behavior. The pinned routing is accepted as documented behavior; restoring PlayVideoIntent on these verbs is Paolo's call with the evidence now in the fixtures.

SPIN-OFF FINDINGS FILED: JF-441 (stable misroute: 'c'e un album chiamato dark side of the moon' -> FindSongByArtistIntent musician slot; explicit album carrier, NOT a JF-418 artifact; catalog-entity weighting suspect). Suite flakiness ledger: 2 of 3 failures were Amazon-side HTTP 500 transients (both re-route correctly on direct probe).

Gates: gate-exempt-shaped (YAML fixtures only, no code); the live suite run IS the verification. Model/template untouched by design.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
