---
id: JF-287
title: Submit jellyfin-alexa-plugin to awesome-jellyfin
status: To Do
assignee: []
created_date: '2026-06-09 15:06'
updated_date: '2026-07-13 20:18'
labels:
  - external
  - awesome-jellyfin
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Open a PR on https://github.com/awesome-jellyfin/awesome-jellyfin to add our jellyfin-alexa-plugin to the curated list.

The plugin fits under **Plugins → Playback** or **Integration & Sync** sections. It's an Alexa skill for voice-controlled media playback, search, and library management on Jellyfin.

Steps:
1. Fork awesome-jellyfin/awesome-jellyfin
2. Add entry to README.md following the existing format and CONTRIBUTING.md guidelines
3. Open PR with descriptive title

Key info for the entry:
- Repo: https://github.com/pantinor/jellyfin-alexa-plugin (or the actual public URL)
- Description: Alexa skill for voice-controlled media playback, search, and library browsing on Jellyfin
- Category: Plugins → Playback (or Integration & Sync)
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
