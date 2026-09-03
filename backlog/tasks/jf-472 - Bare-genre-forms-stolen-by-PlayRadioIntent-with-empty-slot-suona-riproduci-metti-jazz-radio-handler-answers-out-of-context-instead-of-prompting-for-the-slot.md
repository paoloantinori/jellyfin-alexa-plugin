---
id: JF-472
title: >-
  Bare genre forms stolen by PlayRadioIntent with empty slot
  (suona/riproduci/metti jazz); radio handler answers out-of-context instead of
  prompting for the slot
status: In Progress
assignee: []
created_date: '2026-09-03 15:40'
updated_date: '2026-09-03 15:51'
labels: []
dependencies: []
references:
  - 'Device session logs corrs b8e9515d/2c3e0a2a (2026-09-03 17:31/17:34)'
  - Probe matrix (this description)
  - JF-470 (the landscape-shift family)
  - 'CLAUDE.md anti-patterns #1 and #9'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's on-device test session (2026-09-03 ~17:31 and 17:34, corrs b8e9515d, 2c3e0a2a): "suona jazz" (in-skill) selected PlayRadioIntent with the radio slot EMPTY, and the handler spoke the out-of-context error "Nessun contenuto in riproduzione. Riproduci una canzone prima, poi dì riproduci radio" while a disambiguation NoIntent fired around it (user perception: 'impazzisce e da un messaggio di risposta non coerente').

Probe matrix (profile-nlu, 2026-09-03, deterministic 2/2 on the first): 'suona jazz', 'riproduci jazz', 'metti jazz' ALL select PlayRadioIntent with NO slot filled; 'suona musica jazz' correctly selects PlayMoodMusicIntent mood=jazz. PlayRadioIntent has ZERO single-slot samples (its carriers are noun-carrying) and PlayByGenreIntent carries the VERBATIM samples 'Suona {genre}' / 'Riproduci {genre}' / 'Metti {genre}': an intent with no matching sample is winning over a verbatim sample with an empty slot. Same Amazon-side statistical-shift family as JF-470 (catalogs moved v505->v511->v523 in two days).

Two-layer chain, one fixable layer:
1. Amazon-side selection shift: not fixable by samples (the sample exists verbatim); watch for landscape recovery like the JF-470 cases that self-resolved.
2. PLUGIN-FIXABLE (coherence): PlayRadioIntent with an EMPTY radio slot must not run the 'nothing playing' check first; per the anti-pattern #1 boundary the empty slot needs a slot prompt (elicitation: which station?) BEFORE any context check. Check the handler's slot-empty path and the Dialog.ElicitSlot registration for PlayRadioIntent (anti-pattern #9) in all 17 locales if the prompt route is chosen.

Acceptance criteria:
- Unit: PlayRadioIntent with empty station slot + nothing playing -> an elicitation Ask (session open), not the nothing-playing Tell.
- Unit: PlayRadioIntent with empty station slot + something playing -> current behavior unchanged (station-from-context or its existing flow).
- Probe matrix recorded in the fixture/task for the Amazon half; device re-verification obligation for Paolo.
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
