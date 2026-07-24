---
id: JF-365
title: >-
  Per-user config table: collapse dense settings into an expandable row panel
  (row-details pattern)
status: To Do
assignee: []
created_date: '2026-07-24 11:28'
labels:
  - ux
  - config
  - ui
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
UX rework of the per-user config table (config.html). The current table crams 9+ columns into one wide horizontal row per user (User, Skill ID, Invocation Name + description, Libraries, 4 Search Settings, 5 Playback Settings, Status, Token, Actions), forcing horizontal scrolling and making it hard to scan.

DESIGN (Option A — row-expand):
- The collapsed table row shows only the summary: User name, Invocation Name, Status (Ready/Recoverable), and Actions (Delete / Re-authorize). Optionally Skill ID (truncated with tooltip) and Libraries.
- Clicking the row (or a chevron/expand button) reveals a vertical panel BELOW the row containing the per-user settings stacked vertically in logical groups:
  - **Search**: Search Mode, Auto-play match, Auto-play threshold, Suggest threshold
  - **Playback**: Post-Play Behavior, Music delivery, Announce position, Announce now-playing, Announce music
  - **Invocation name**: the text input + Reset + description (currently the widest column)
- This is the DataTables row-details pattern: a hidden `<tr>` or `<div>` toggled by a click on the summary row.
- The existing JS load/collect/payload wiring stays intact (same element IDs, same collect logic), just relocated from inline table cells to the expand panel.
- Apply the same example-utterance fieldDescriptions from the global settings (JF-364) to the per-user equivalents where they exist (search mode, post-play, etc.).

CONSTRAINTS:
- Jellyfin-embedded admin HTML using emby web components. No framework.
- Mockup-screenshot before applying (standing order on UI changes).
- The table must still work with multiple users (the expand should be independent per row).
- Status indicators (Ready/Recoverable/Error) must remain visible in the collapsed row — they're the most important at-a-glance info.
- The "Add New User Skill" and "Account Linking" sections below the table are unaffected.

ACCEPTANCE:
- [ ] Collapsed row shows: User, Invocation Name, Status, Actions. No horizontal scroll on a 1280px screen.
- [ ] Expanding a row reveals all per-user settings in a vertical panel, grouped by Search / Playback / Invocation.
- [ ] All settings round-trip correctly (load from config, save via PATCH, reload confirms).
- [ ] Multiple users: each row expands/collapses independently.
- [ ] Status remains visible without expanding.
- [ ] Example utterances on the per-user settings that mirror the global ones.

VERIFICATION:
- HTML mockup served over http + Playwright screenshot before touching the real file.
- Deploy to minix, load config page, expand/collapse, verify value round-trip.
- Full dotnet build (config.html embeds as resource).

OUT OF SCOPE:
- Global settings reorganization (done in JF-364).
- A full visual redesign of the config page.
- Per-user feature-flag overrides (those live in the global table, not the per-user row).

Related: JF-364 (global config regroup + example utterances).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
