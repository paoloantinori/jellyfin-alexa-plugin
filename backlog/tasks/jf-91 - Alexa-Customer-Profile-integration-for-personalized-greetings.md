---
id: JF-91
title: Alexa Customer Profile integration for personalized greetings
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-07 01:07'
labels:
  - user-experience
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use the `Alexa.NET.Profile` package to access customer name, email, and timezone. Primary use cases:
- Personalized greetings ("Hi, Paolo!") on skill launch
- Timezone-aware scheduling for sleep timer and content release notifications
- More natural interaction when the skill knows the user's name

This is a polish/UX feature, not critical functionality.

Files: New profile utility class, integration into launch request handler, skill manifest update.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 4.3
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Alexa.NET.Profile NuGet package added to csproj
- [ ] #2 Skill manifest updated with profile read permissions
- [ ] #3 Personalized greeting using customer's first name on skill launch
- [ ] #4 Timezone-aware scheduling for time-sensitive features (sleep timer, notifications)
- [ ] #5 Graceful fallback when profile access is denied or unavailable
- [ ] #6 Unit tests with mocked profile API responses
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
