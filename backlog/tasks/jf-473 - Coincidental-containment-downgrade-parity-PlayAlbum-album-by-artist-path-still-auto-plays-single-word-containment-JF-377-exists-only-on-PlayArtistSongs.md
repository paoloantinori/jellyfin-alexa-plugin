---
id: JF-473
title: >-
  Coincidental-containment downgrade parity: PlayAlbum album-by-artist path
  still auto-plays single-word containment (JF-377 exists only on
  PlayArtistSongs)
status: To Do
assignee: []
created_date: '2026-09-03 16:24'
labels: []
dependencies: []
references:
  - JF-471 (the acceptance gate this extends)
  - JF-377 (the coincidental-containment downgrade on PlayArtistSongs)
  - JF-420 (containment-vs-full-name fair comparison)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Residual gap surfaced by the JF-471 implementation (2026-09-03): the new PassesArtistMatchAcceptance gate on PlayAlbum's album-by-artist path refuses the word-coverage free pass (below-threshold matches like 'dark side of the moon' -> 'Dark Dark Dark' now clean-not-found), but a single-word artist whose name IS genuinely CONTAINED in a stolen album span still auto-plays at containment 90 (e.g. an artist literally named 'Dark' for query 'dark side of the moon'; containment >= 90 by construction, so the acceptance gate passes it).

The JF-377 coincidental-containment downgrade (yes/no AskFirstMatch prompt instead of auto-play) exists ONLY on PlayArtistSongs' tier-4 path. This task is the consistency-parity decision: port the JF-377 downgrade (or its JF-420-era refinements) to the album-by-artist acceptance point so both paths treat coincidental containment the same way.

Before implementing: reproduce the containment case at unit level (an artist named exactly one word of a multi-word stolen span, score >= 90 via containment), then apply the same downgrade shape PlayArtistSongs uses, keeping the legit single-word containment flows working (ASR truncation 'crash' -> 'Crash Test Dummies' is prefix-shaped, not containment; verify the distinction the JF-377 research already established: bug cases and carrier-bleed cases are string-indistinguishable, hence the prompt, never a reject).

Acceptance criteria:
- Unit: artist='Dark', query='dark side of the moon' -> the yes/no prompt (not auto-play), matching PlayArtistSongs' behavior for the same shape.
- Unit: the JF-471 gate's own tests stay green (no regression of the free-pass refusal).
- The legit containment classes documented in the JF-377/JF-420 research still auto-play.
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
