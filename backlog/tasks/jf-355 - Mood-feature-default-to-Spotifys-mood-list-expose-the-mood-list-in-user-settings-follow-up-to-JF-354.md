---
id: JF-355
title: >-
  Mood feature: default to Spotify's mood list + expose the mood list in user
  settings (follow-up to JF-354)
status: To Do
assignee: []
created_date: '2026-07-20 07:43'
labels:
  - mood
  - config
  - per-user
  - spotify
  - follow-up
  - i18n
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up to JF-354 (the custom Mood slot type). Two refinements the maintainer wants:

1. **Default the Mood slot-type values to Spotify's mood list** (the golden standard). Research Spotify's supported moods (Happy, Sad, Chill/Relax, Workout/Energy, Focus, Party, Sleep, Romance, etc.) + map each to the handler's genre arrays (MoodGenreMap). The current Mood values (JF-354) are the LocalizedMoodMap keys; replace/augment with the Spotify-standard list so users get a familiar vocabulary. Localize for all 17 locales.

2. **Expose the mood list in user settings** (configurable per-user): a user can add/remove/customize moods. This likely needs the Mood slot type to be populated per-user (dynamic-entity updates via SMAPI at session start, OR a global default + per-user overrides). Assess the mechanism (dynamic entities vs per-user model slice).

Do this AFTER JF-354's pioneer (it-IT custom Mood type) is verified + rolled to 17 locales. Not blocking the pioneer.
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
