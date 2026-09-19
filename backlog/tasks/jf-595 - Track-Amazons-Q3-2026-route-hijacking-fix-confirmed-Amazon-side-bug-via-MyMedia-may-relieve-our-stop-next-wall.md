---
id: JF-595
title: >-
  Track Amazon's Q3-2026 route-hijacking fix (confirmed Amazon-side bug via
  MyMedia): may relieve our stop/next wall
status: To Do
assignee: []
created_date: '2026-09-19 14:08'
updated_date: '2026-09-19 14:12'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-19 BASELINE captured (delegation to hermes-fleet agent decided by Paolo): alexa-plus.html scraped live - page Last Updated 6/25/26 (untouched ~3 months); the Q3 sentence verbatim: 'We escalated the route hijacking to Amazon who analyzed sample user sessions and confirmed it is a bug Amazon side and not in our skill. They have proposed a 2026Q3 fix timeframe, however, offer no guarantees.' NEW CONTEXT: the hijacking is tied to Alexa+ introduction ('in the past 6 months, with the introduction of Alexa+... Other intents are massively deprioritized'); MyMedia's workaround is the ARH multi-turn dialog model (confirms our two-step convention as the right mitigation); their Step 3 explicitly says presentation 'will depend on whether Amazon resolves the hijacking issue' - so the page updating at all is a secondary signal. Monitoring handed to the hermes agent with a self-contained prompt (baseline quotes + diff-based drift detection); JF-595 stays open here and gets updated when hermes reports a change; the on-device stop/next battery (AC#2) remains ours.
<!-- SECTION:NOTES:END -->

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
