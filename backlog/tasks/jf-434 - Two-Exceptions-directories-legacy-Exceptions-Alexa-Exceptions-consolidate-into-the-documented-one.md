---
id: JF-434
title: >-
  Two Exceptions directories (legacy Exceptions/ + Alexa/Exceptions/):
  consolidate into the documented one
status: Done
assignee:
  - zai
created_date: '2026-09-01 06:07'
updated_date: '2026-09-01 21:00'
labels:
  - cleanup
  - conventions
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Exceptions/JsonParsingException.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Exceptions/SkillWarmingUpException.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Trivial convention cleanup flagged by the JF-419.2 angle-D reuse review (2026-08-31) and never tracked until the 2026-09-01 audit: the repo has TWO exception directories - the legacy Jellyfin.Plugin.AlexaSkill/Exceptions/ (JsonParsingException.cs, live: thrown at Util.cs:53) and the new Alexa/Exceptions/ (SkillWarmingUpException, added by JF-419.2; the layout CLAUDE.md documents). The next custom exception gets filed by coin flip until one location wins. One 5-minute move; kept as its own task because it touches neither the dialog consolidation (JF-430) nor the controller cleanup (JF-421).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 JsonParsingException moves under Alexa/Exceptions/ (namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions), its single throw site (Util.cs) and any usings updated
- [x] #2 The old Exceptions/ directory is gone; CLAUDE.md's documented layout (Alexa/Exceptions/) becomes the single truth
- [x] #3 grep confirms one Exceptions namespace remains project-wide
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-434: one Exceptions directory, the documented one.

WHAT CHANGED (commit 47594e8)
- JsonParsingException.cs moved (git mv, history preserved) from the legacy Jellyfin.Plugin.AlexaSkill/Exceptions/ to Alexa/Exceptions/, namespace updated to Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions; the single consumer's using (Util.cs) updated, StyleCop order preserved. Old directory gone; solution-wide grep confirms one Exceptions namespace matching the CLAUDE.md layout.

VERIFICATION
- Build 0 warnings/0 errors (warnaserror on); unit suite 2812/2812.
- Gates: gate-exempt by the trivial-fix rule (semantic diff = 2 lines: one namespace, one using); the same code-review run that covered JF-435 swept the move and reported zero findings on it.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
