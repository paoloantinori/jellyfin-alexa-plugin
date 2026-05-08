---
id: JF-98
title: Audit all locale interaction models for handler dispatch and NLU coverage
status: In Progress
assignee: []
created_date: '2026-05-08 18:42'
updated_date: '2026-05-08 18:42'
labels:
  - testing
  - nlu
  - multi-locale
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The FallbackIntentHandler bug was language-independent (C# handler dispatch), but we should verify each locale's interaction model has proper utterance coverage for the main intents (PlayArtistSongs, PlaySong, PlayAlbum, etc.) and that SMAPI simulations resolve correctly. Check all 12 locales for: 1) PlayArtistSongsIntent utterances with catalog-backed slot types 2) Built-in intent handlers working (Yes/No/Pause/Resume) 3) Any locale-specific NLU gaps
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
