---
id: JF-287
title: Submit jellyfin-alexa-plugin to awesome-jellyfin
status: In Progress
assignee: []
created_date: '2026-06-09 15:06'
updated_date: '2026-10-18 05:35'
labels:
  - external
  - awesome-jellyfin
  - 'gate:maintainer-approval'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SUBMITTED 2026-10-12 (maintainer's blanket approval, the JF-855 discussion round): upstream issue awesome-jellyfin/awesome-jellyfin#694 filed, PR awesome-jellyfin/awesome-jellyfin#695 opened from the fork (branch add-jellyfin-alexa-plugin, one line in the playback section between intro-skipper and cinema mode, conventional-commits per CONTRIBUTING.md). Awaiting upstream review. DoD #8's '12 locales' is stale (the model carries 17 since JF-316).

UPSTREAM TRACKING 2026-10-18 ~08:1x (maintainer asked 'fai 287'; the notes-first check showed submission already done 2026-10-12): PR awesome-jellyfin/awesome-jellyfin#695 re-verified live - OPEN, +1/-0, reviewer TomRomeo requested, ZERO inline comments, ZERO issue comments, ZERO reviews after 6 days. Nothing to respond to; the row stays In Progress as an upstream-tracking tracker until the PR merges or feedback lands. Re-check cadence: on request or with the weekly sweep.
<!-- SECTION:NOTES:END -->
