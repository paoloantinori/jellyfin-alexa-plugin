---
id: JF-595
title: >-
  Track Amazon's Q3-2026 route-hijacking fix (confirmed Amazon-side bug via
  MyMedia): may relieve our stop/next wall
status: To Do
assignee: []
created_date: '2026-09-19 14:08'
labels:
  - research
  - alexa-platform
  - on-device
milestone: Polish
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Adjacent finding from the JF-561 MSAPI research (2026-09-19): MyMedia for Alexa's June 2026 page documents that Amazon ANALYZED their route-hijacing samples and "confirmed it is a bug Amazon side and not in our skill. They have proposed a 2026Q3 fix timeframe." Q3 2026 ends Sept 30 - days away. If the fix landed and covers custom AudioPlayer skills generally (not just MyMedia), the #1 platform wall of this project (bare stop/next claimed by the default music service during our playback, verified on-device 3x since 2026-07) may be RELIEVED, and the main motivation for ever considering MSAPI evaporates. Source: docs.bizmodeller.com/my-media-for-alexa/alexa-plus.html.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 bizmodeller.com/my-media-for-alexa/alexa-plus.html re-read and the fix status recorded (shipped / slipped / silent)
- [ ] #2 If shipped: on-device battery during OUR AudioPlayer playback - bare 'stop', 'next', 'ferma', 'avanti' - log-verified whether requests now reach the skill
- [ ] #3 Outcome recorded in the CLAUDE.md stop-routing reference section (either the wall is relieved or it stands, with evidence)
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
