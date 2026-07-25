---
id: JF-372
title: >-
  Tri-state selects silently downgrade to 'inherit' if the emby-select fails to
  upgrade
status: Done
assignee: []
created_date: '2026-07-24 19:32'
updated_date: '2026-07-25 04:59'
labels:
  - config
  - robustness
  - data-integrity
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
In the save loop, the tri-state selects (VideoAppForAudio, AnnounceNowPlaying, AnnounceAudioPlays) are read via `triState(sel)` which returns null for anything other than "true"/"false" — including when the select element exists but failed to upgrade (e.g. an emby-select component-upgrade failure, the labelElement class of bug we hit this session). In that case `sel.value` may be undefined/stale and triState() returns null, which serializes to JSON null = "inherit global default". So a component-upgrade failure silently rewrites a user's explicit true/false override to "use global default" on save — silent data drift, no error.

We fixed the known labelElement crashes, but the failure class remains. FIX options: (a) before reading, verify the select actually upgraded (has the emby-select class / expected option values) and if not, alert + skip rather than defaulting to inherit; (b) treat an unrecognized value as a hard error instead of inherit. Lower priority since the known crashes are fixed, but worth a defensive check given how brittle the emby component contract proved to be.

Closely related to JF-366 (testability) and the config-page-style-not-applied memory: the panel's reliance on emby component upgrades is the systemic risk.
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
