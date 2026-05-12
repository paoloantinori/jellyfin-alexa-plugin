---
id: JF-132
title: >-
  Fix NLU ambiguity: en-US "play bohemian rhapsody by queen" resolves to
  PlayArtistSongsIntent instead of PlaySongIntent
status: To Do
assignee: []
created_date: '2026-05-12 09:41'
labels:
  - nlu
  - en-US
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The utterance "play bohemian rhapsody by queen" (en-US) resolves to PlayArtistSongsIntent instead of PlaySongIntent. The "by queen" phrase triggers artist matching, overriding the song intent.

The interaction model needs better disambiguation between PlaySongIntent and PlayArtistSongsIntent when both a song name and artist are present. Consider adding more concrete sample utterances with "song" keyword for PlaySongIntent.

NLU test fixture: en-US - "play bohemian rhapsody by queen" (expected PlaySongIntent, got PlayArtistSongsIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
