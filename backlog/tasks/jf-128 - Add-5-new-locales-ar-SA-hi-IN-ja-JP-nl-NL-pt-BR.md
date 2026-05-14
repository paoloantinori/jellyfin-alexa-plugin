---
id: JF-128
title: 'Add 5 new locales: ar-SA, hi-IN, ja-JP, nl-NL, pt-BR'
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 13:15'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added ar-SA, hi-IN, ja-JP, nl-NL, pt-BR locales with 169 response strings each and full interaction models. csproj globs auto-discover new files. 1181 tests pass.
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
