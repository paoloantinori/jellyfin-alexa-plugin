---
id: JF-434
title: >-
  Two Exceptions directories (legacy Exceptions/ + Alexa/Exceptions/):
  consolidate into the documented one
status: To Do
assignee: []
created_date: '2026-09-01 06:07'
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
- [ ] #1 JsonParsingException moves under Alexa/Exceptions/ (namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions), its single throw site (Util.cs) and any usings updated
- [ ] #2 The old Exceptions/ directory is gone; CLAUDE.md's documented layout (Alexa/Exceptions/) becomes the single truth
- [ ] #3 grep confirms one Exceptions namespace remains project-wide
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
