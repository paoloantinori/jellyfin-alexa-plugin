---
id: JF-348
title: >-
  Rebuild All Models button - honest label and all-locales option (follow-up to
  PR #14)
status: Done
assignee: []
created_date: '2026-07-16 17:06'
updated_date: '2026-09-03 16:49'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and verified (commit 7a003fbe).

UX: two explicit sibling buttons. 'Rebuild Selected Locale' already existed (AC#1 pre-satisfied, verified); new 'Rebuild All Locales' button in the same flex row, matching the page's action-button idiom and its existing '*' = all convention. Wire contract: locale:'*' sentinel; the controller's new internal ResolveRebuildLocaleFilter maps it to localeFilter=null (every embedded locale model, verified in source: null skips none of the 17). Backward compatible byte-identically: locale present = that locale; absent/blank = CustomModelLocale fallback (blank now normalized to null, downstream-equivalent); repo-wide sweep found no producer of a literal '*' so nothing can silently switch scope.

Controller tests: 4 methods / 6 cases over the extracted pure function (no endpoint infrastructure exists: the controller hard-depends on Plugin.Instance and the concrete ModelDeploymentManager; InternalsVisibleTo used rather than building WebApplicationFactory scaffolding). The stale deploy and rebuild-models runbooks (curls sent no locale, silently rebuilding only CustomModelLocale) now document all three request shapes.

Deploy verification (orchestrator): DLL deleted before Release build per the embedded-resource protocol (strings counts: rebuildAllModelsButton=3, Rebuild All Locales=2); served page post-deploy greps rebuildAllModelsButton=3; the sentinel rebuild end-to-end reported 'Rebuilt 17 locale(s), 12 succeeded, 0 failed' (12 = the live skill's active locales), which is exactly the all-locales path this task restores. Suite 3101/3101, Release 0 warnings, validators baseline.

Gates: /simplify (worker self-run, 2 skips documented: runbook text duplication mirroring existing snippet duplication; early-return form); code-review self-run high effort, zero findings >= 80 (sub-threshold: concurrent-button-click exposure is pre-existing and shared by all four model buttons; garbage-locale empty rebuild unchanged by design).
<!-- SECTION:FINAL_SUMMARY:END -->

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
