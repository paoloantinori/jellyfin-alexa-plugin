---
id: JF-87
title: Update APL to latest version and add interactive controls
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-06 22:45'
labels:
  - apl
  - user-experience
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Update APL (Alexa Presentation Language) from version 1.4 to latest (1.8+), and add interactive features for Echo Show devices.

Current state: APL version 1.4 with Now Playing screen and queue list using `alexa-layouts` import.

Opportunities:
- Use responsive components (AlexaHeader, AlexaText, AlexaButton) for consistent styling
- Add viewport profiles for responsive design across screen sizes
- Add interactive APL touch handlers for playback controls (play/pause, next/prev)
- APL for Audio for richer audio responses

Files: `Alexa/AplHelper.cs`, APL document JSON templates, handler files using APL.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 4.2
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 APL document version updated from 1.4 to latest stable (1.8+)
- [ ] #2 AlexaHeader, AlexaText responsive components used where applicable
- [ ] #3 Touch handlers added for playback controls (play/pause, next/prev) on Echo Show
- [ ] #4 viewportProfile used for responsive design across screen sizes
- [ ] #5 Existing APL tests pass; new tests for touch handler directives
- [ ] #6 All 12 locale APL documents updated consistently
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
