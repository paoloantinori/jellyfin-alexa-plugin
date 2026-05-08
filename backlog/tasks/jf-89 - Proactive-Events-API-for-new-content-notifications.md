---
id: JF-89
title: Proactive Events API for new content notifications
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-07 01:07'
labels:
  - new-feature
  - proactive-events
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement Proactive Events API to push notifications to users without an active session. Users opt-in via the Alexa app.

Use cases for Jellyfin:
- "New episodes of [show you watch] are available"
- "Your favorite artist released a new album"
- "A new movie was added to your library"

Technical approach: Use the `Alexa.NET.ProactiveEvents` NuGet package. Requires:
1. Adding `alexa::alerts:skillnotifications:write` permission to skill manifest
2. Storing user consent tokens
3. Periodic background check (Jellyfin plugin scheduled task) for new content
4. Rate limit: 10 events/user/hour, 50/user/day

This is a large effort but high-impact feature that transforms the skill from purely reactive to proactive.

Files: New `ProactiveEvents/` directory, skill manifest, `SkillStartup.cs` for background task registration.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 4.1
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Alexa.NET.ProactiveEvents NuGet package added to csproj
- [ ] #2 Skill manifest updated with alexa::alerts:skillnotifications:write permission
- [ ] #3 Background scheduled task checks for new content matching user preferences
- [ ] #4 Proactive event sent when new episodes/albums/movies match user watchlist
- [ ] #5 Rate limiting respected (10 events/user/hour, 50/user/day)
- [ ] #6 User consent token storage and retrieval implemented
- [ ] #7 Unit tests for event generation and rate limiting
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
