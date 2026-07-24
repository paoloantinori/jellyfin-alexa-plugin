---
id: JF-274
title: Verify Go To Chapter navigation
status: To Do
assignee: []
created_date: '2026-06-08 09:31'
updated_date: '2026-07-13 20:17'
labels:
  - e2e
  - audiobooks
milestone: m-4
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/GoToChapterIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GoToChapterIntentHandler handles chapter navigation for audiobooks/videos. No E2E coverage. Need to:
1. Test "next chapter" / "previous chapter" with an audiobook
2. Test "go to chapter 5" with specific chapter number
3. Verify playback resumes at correct position after chapter skip
4. Test edge cases: first chapter (prev), last chapter (next), invalid chapter number
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
