---
id: JF-348
title: >-
  Rebuild All Models button - honest label and all-locales option (follow-up to
  PR #14)
status: To Do
assignee: []
created_date: '2026-07-16 17:06'
labels: []
dependencies: []
references:
  - 'PR #14'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PR #14 scopes the manual rebuild to the configured locale (a useful optimization, assuming the invocation-name redeploy path is kept all-locales per the review). As noted in the #14 review, the "Rebuild All Models" button (config.html:293) then becomes a misnomer and removes the only all-locales rebuild path (per-locale deploy already exists via the Deploy/Restore buttons). Goal: make the button label match its real behavior (e.g. "Rebuild Selected Locale") and/or add an explicit "all locales" rebuild option alongside, so the UI stays honest and power users can still force a full rebuild. Depends on PR #14 landing with the locale-scoped manual rebuild.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Button label matches actual behavior (no 'All' when it rebuilds a single locale)
- [ ] #2 An explicit way to rebuild all locales is preserved (toggle, separate button, or option)
- [ ] #3 config.html JS and ConfigurationController.RebuildModels stay consistent with the chosen UX
- [ ] #4 Manually verified: rebuild-selected-locale and rebuild-all both behave exactly as labeled
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
