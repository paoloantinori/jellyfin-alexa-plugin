---
id: JF-90
title: Alexa.NET.Reminders integration for native reminders
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-07 01:07'
labels:
  - new-feature
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Use the `Alexa.NET.Reminders` NuGet package to set time-based native Alexa reminders. Could be used to:
- Remind users about new episodes of shows they watch
- Provide sleep timer functionality via native reminders rather than custom implementation
- Alert users about upcoming content releases

The sleep timer integration is particularly interesting — instead of managing timers server-side, delegate to Alexa's native reminder system.

Files: New reminder utility class, integration into sleep timer handler (if exists), skill manifest update.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 4.2 / extension table
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Alexa.NET.Reminders NuGet package added to csproj
- [ ] #2 Skill manifest updated with reminders permission
- [ ] #3 Users can set reminders tied to content (e.g., 'remind me when new episode drops')
- [ ] #4 Sleep timer intent can optionally set a native reminder for 'you've been listening for X minutes'
- [ ] #5 Reminder creation handles permission denial gracefully
- [ ] #6 Unit tests for reminder creation and error handling
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
