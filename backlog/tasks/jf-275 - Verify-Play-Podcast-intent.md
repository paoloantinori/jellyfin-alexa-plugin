---
id: JF-275
title: Verify Play Podcast intent
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:17'
labels:
  - e2e
  - podcasts
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PlayPodcastIntentHandler plays podcast episodes. No E2E coverage. Need to:
1. Test "play podcast [name]" with a valid podcast in library
2. Test "play latest episode of [name]"
3. Verify fuzzy matching for podcast names
4. Test when podcast has no episodes
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
