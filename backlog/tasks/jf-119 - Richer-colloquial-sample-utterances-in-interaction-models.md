---
id: JF-119
title: Richer colloquial sample utterances in interaction models
status: Done
assignee: []
created_date: '2026-05-12 04:44'
updated_date: '2026-05-12 11:45'
labels:
  - enhancement
  - nlu
  - interaction-model
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add more creative and colloquial phrasing to interaction model sample utterances to improve NLU accuracy. Current patterns are formal; adding variations like "put on {artist}", "give me some {artist}", "I want to hear {artist}" would capture how people naturally speak.

Inspired by JellyMusic's rich utterance set which includes casual phrasing alongside formal patterns.

Implementation: Update YAML templates in `templates/` with additional colloquial utterance patterns for all play/search intents. Regenerate models via `generate_interaction_model.py`. Deploy via `validate_model.sh`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All play intents have at least 15 sample utterances including colloquial variations
- [ ] #2 Colloquial patterns added for en-US, en-GB, it-IT at minimum
- [ ] #3 NLU test fixtures updated with new utterance patterns
- [ ] #4 Models regenerated and validated via validate_model.sh
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added colloquial utterances to 3 locales. en-US: 5 intents enriched (PlayPlaylist 2→20, PlayLastAdded 12→25, PlayByGenre 6→16, PlayRandom 14→23, PlayVideo 1→15). en-GB: 8 intents enriched with British colloquialisms (stick on, bang on, fancy). it-IT: 3 intents enriched with Italian colloquial patterns. All play intents now have 15+ samples. Build clean, 1019 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
