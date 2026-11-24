---
id: JF-958
title: >-
  es episode family stolen by naked genre carriers (Reproduce/Pon {genre});
  qualification needs the maintainer's carrier decision
status: Done
assignee: []
created_date: '2026-11-24 20:13'
updated_date: '2026-11-24 21:04'
labels:
  - nlu
  - es
  - carrier-steal
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live-diagnosed 2026-11-24 after the full 17-locale model rebuild (all SUCCEEDED): the es episode family is stolen by the NAKED genre carriers. Suite evidence: 11 rows across es-ES (4), es-MX (3), es-US (5) of the shape 'reproduce/pon/que reproduzca adolescencia temporada uno episodio tres' expect PlayEpisodeIntent and read PlayByGenreIntent, with genre='adolescencia' filling the free-text AMAZON.Genre slot while PlayEpisodeIntent sits at rank 2 with every slot correctly filled (episode_number=3, season_number, series_name). Root cause: PlayByGenreIntent in the es locales carries the naked carriers 'Reproduce {genre}' and 'Pon {genre}' (es-ES model has exactly 5 samples, two of them naked), so the two-token greedy carrier beats PlayEpisode's exact 4-slot sample 'reproduce {series_name} temporada {season_number} episodio {episode_number}' in the trainer's competition. The LAB routes the same utterances correctly (ablation 2026-11-24 13:39) because the catalog wiring differs; on the live skill the SeriesName catalog wiring tips the marginal competition. The fix class is the b6da49e7 naked-carrier qualification: 'Reproduce {genre}' -> 'Reproduce musica {genre}' (the qualified form already exists), same for 'Pon {genre}', mirrored in es-MX/es-US. MAINTAINER DECISION REQUIRED per the standing carrier rule (2026-11-13: any carrier change needs at least one real user involved): qualifying the naked forms trades the bare 'reproduce rock' one-shot for 'reproduce musica rock' against fixing the episode steal. Do not edit the templates before that decision.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: template-only change; the applicable verification is the live deploy + model rebuild + profile-nlu probe sequence, all in this session's transcript; no C# or test code changed.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-24 after the maintainer's decision ('958 okay per il sostantivo'): the naked carriers 'Reproduce {genre}' and 'Pon {genre}' in the three es templates are qualified to 'Pon musica {genre}' (the qualified 'Reproduce musica {genre}' already existed, so es-ES net-loses one sample); the JF-958 comment block documents the steal, the decision and the JF-960 restoration path. Deployed (DLL into AlexaSkill_1.1.2.0, config intact) and all three es models rebuilt SUCCEEDED. Live proof post-rebuild: the five previously-stolen episode rows ('reproduce/pon/que reproduzca adolescencia temporada uno episodio tres' in es-ES, 'reproduce...' in es-MX and es-US) all route to PlayEpisodeIntent at rank 1, and the qualified genre forms ('reproduce musica rock' es-ES, 'reproduce musica jazz' es-US) still route to PlayByGenreIntent: both sides of the accepted trade verified. Validator PASS, nlu-audit clean, models regenerated from templates. gate-exempt: template-only change verified by the live deploy+rebuild+probe sequence in this session's transcript (the verification that applies to model changes); no production code touched.
<!-- SECTION:FINAL_SUMMARY:END -->
