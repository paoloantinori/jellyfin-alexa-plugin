---
id: JF-76
title: Enhanced themed / contextual playlists with time-of-day awareness
status: Done
assignee: []
created_date: '2026-05-04 19:01'
updated_date: '2026-05-05 22:58'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enhance PlayMoodMusicIntent with more mood categories and time-of-day awareness. Users should be able to say "Play workout music", "Play dinner music", "Play focus music", "Play morning music". The existing intent is partially implemented — the enhancement adds broader mood categories and contextual awareness (time of day) to provide more relevant suggestions.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Additional mood categories: workout, dinner, focus, party, relax, morning, evening
- [ ] #2 Time-of-day awareness automatically biases playlist selection (e.g., 'morning music' at 7am vs 9pm)
- [ ] #3 PlayMoodMusicIntent maps new utterances to Jellyfin library queries via tags/genres
- [ ] #4 Graceful fallback when library lacks tagged content for a given mood
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Enhanced PlayMoodMusicIntentHandler with morning/evening/dinner mood mappings and time-of-day genre bias. Time-aware reordering searches preferred genres first based on hour (morning: acoustic/pop/folk; afternoon: rock/electronic; evening: jazz/ambient). 15 unit tests, 6 live NLU tests passing. Committed as 9a5e3b9.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
