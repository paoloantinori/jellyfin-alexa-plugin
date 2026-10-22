---
id: JF-911
title: >-
  JF-876 follow-ups: movie-family ranks 4+ unreachable (cap 3, no live case) +
  the gruffalo e2e row when SMAPI returns
status: To Do
assignee: []
created_date: '2026-10-19 20:54'
updated_date: '2026-10-22 13:52'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-876/JF-877 merge (a7a5c854, 2026-10-19 night; filed same-turn per the every-recommendation-lands rule; two low-risk residuals the code review surfaced and the merge deliberately deferred).

(1) CAP-DROP RESIDUAL: the movie multi-value ER resolver breaks at FirstMatchStateCap (3), matching the JF-735 verdict's yes/no cycle shape, so ranks 4+ of a same-first-word movie family are neither asked nor stored; the cycle ends NoMoreMatches while the movie exists and re-asking re-enters the same gate. The live census's largest family is 'avventure' with 3 movies (one short of the cap), so no live case exists today. Remedy shape if a 4+ family ever lands: widen the state cap or speak the total count; re-weigh the JF-735 verdict then. Same shape already true on the artist path (not a movie-specific defect).

(2) E2E ROW when SMAPI returns: add the live e2e_it-IT.yaml row for the movie ask ('chiedi a mia collezione di mettere il film gruffalò' -> PlayMovieIntent with the ask response, or the in-session shape the suite uses for disambiguation rows). Unshippable the night of the merge (fr/it simulate router in the per-call selection-lost outage; an unverifiable expectation would go stale silently per the JF-769-class discipline). The DEVICE PROBE for Paolo's morning round is the interim verification: 'chiedi a mia collezione di mettere il film gruffalò' must now ANSWER THE WHICH-ONE QUESTION (it-IT DisambiguatePrompt naming 'Il Gruffalò e la sua piccolina' first) instead of silently playing the sequel; 'sì' plays the rank-1, 'no' advances to 'Il Gruffalò'.
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
DEVICE ROUND 2026-10-22 15:31 (Paolo): 'chiedi a mia collezione di mettere il film gruffalò' PLAYED THE ORIGINAL DIRECTLY, no ask (corr 65abaa84). VERDICT: CORRECT BEHAVIOR, not a gate failure. The device delivered the OTHER JF-874 twin: PlayVideoIntent with free-text title='gruffalo' (no ER resolutions at all; the trainer coin-flip between the duplicated 'Di mettere il film' sample claims went to the free-text twin today), where the JF-876 ER gate does not exist by construction. On that path the exact search missed and HandleFuzzyMiss scored 'Il Gruffalo' 90, hitting the documented auto-play bar (>= 90, no qualifier) - it played the ORIGINAL, the film the name said. The ask (JF-876) remains SIMULATOR-VERIFIED for the catalog-twin delivery shape (the multi-value ER); a device confirmation of the ASK itself remains outstanding and not directly triggerable (it lands whenever the trainer awards the utterance to the PlayMovie twin). The test instruction for the device round promised the ask unconditionally - that was the error, corrected here: the ask is expected ONLY on the catalog-ER delivery.
<!-- SECTION:NOTES:END -->
