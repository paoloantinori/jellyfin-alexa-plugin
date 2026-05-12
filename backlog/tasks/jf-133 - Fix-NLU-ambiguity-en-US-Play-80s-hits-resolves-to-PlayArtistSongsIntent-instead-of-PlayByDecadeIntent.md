---
id: JF-133
title: >-
  Fix NLU ambiguity: en-US "Play 80s hits" resolves to PlayArtistSongsIntent
  instead of PlayByDecadeIntent
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
The utterance "Play 80s hits" (en-US) resolves to PlayArtistSongsIntent instead of PlayByDecadeIntent. The word "hits" likely matches generic song/artist patterns.

The interaction model needs better disambiguation for decade-based utterances. Consider adding more decade-specific sample utterances or adjusting PlayArtistSongsIntent to avoid matching decade patterns.

NLU test fixture: en-US - "Play 80s hits" (expected PlayByDecadeIntent, got PlayArtistSongsIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
