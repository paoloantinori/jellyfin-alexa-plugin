---
id: JF-71
title: Podcast support — browse and play podcasts
status: Done
assignee: []
created_date: '2026-05-04 19:01'
updated_date: '2026-05-06 11:12'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add PlayPodcastIntent to browse and play podcasts from Jellyfin libraries. Jellyfin has podcast support via its library system. Users should be able to say "Play the podcast [name]" or "Play the latest episode of [podcast]" and have the skill search Jellyfin's podcast library and start playback. Podcasts are a major use case for voice assistants, making this high impact.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 PlayPodcastIntent handles utterances like 'Play the podcast [name]' and 'Play the latest episode of [podcast]'
- [x] #2 Skill queries Jellyfin API for podcast libraries and retrieves episodes
- [x] #3 Playback starts the latest episode or a specific podcast by name
- [x] #4 Graceful response when no matching podcast is found in the library
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
