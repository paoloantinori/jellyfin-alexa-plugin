---
id: JF-119
title: Richer colloquial sample utterances in interaction models
status: To Do
assignee: []
created_date: '2026-05-12 04:44'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
