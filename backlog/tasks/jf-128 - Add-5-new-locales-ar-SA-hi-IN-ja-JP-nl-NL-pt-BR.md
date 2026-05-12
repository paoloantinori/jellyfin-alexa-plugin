---
id: JF-128
title: 'Add 5 new locales: ar-SA, hi-IN, ja-JP, nl-NL, pt-BR'
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - localization
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Expand locale coverage from 12 to 17 locales by adding: ar-SA (Arabic), hi-IN (Hindi), ja-JP (Japanese), nl-NL (Dutch), and pt-BR (Brazilian Portuguese). These represent significant user populations currently uncovered.

Inspired by AskPlex which supports 16 locales including all of these. Their language interceptor with locale fallback makes adding partial translations lower-risk.

Implementation: Create YAML templates for each new locale, generate interaction models, add response string translations. Machine translation is acceptable as a starting point with native speaker review noted as follow-up.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Interaction models and response strings added for ar-SA (Arabic)
- [ ] #2 Interaction models and response strings added for hi-IN (Hindi)
- [ ] #3 Interaction models and response strings added for ja-JP (Japanese)
- [ ] #4 Interaction models and response strings added for nl-NL (Dutch)
- [ ] #5 Interaction models and response strings added for pt-BR (Brazilian Portuguese)
- [ ] #6 New models validated via validate_model.sh
- [ ] #7 NLU test fixtures added for each new locale
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
