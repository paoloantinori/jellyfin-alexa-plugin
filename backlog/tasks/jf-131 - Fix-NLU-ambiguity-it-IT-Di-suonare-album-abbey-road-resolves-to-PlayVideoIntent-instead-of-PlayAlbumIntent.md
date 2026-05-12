---
id: JF-131
title: >-
  Fix NLU ambiguity: it-IT "Di suonare album abbey road" resolves to
  PlayVideoIntent instead of PlayAlbumIntent
status: To Do
assignee: []
created_date: '2026-05-12 09:41'
labels:
  - nlu
  - it-IT
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The utterance "Di suonare album abbey road" (it-IT) resolves to PlayVideoIntent instead of PlayAlbumIntent. This is an NLU disambiguation issue — Alexa's NLU picks the generic PlayVideoIntent over PlayAlbumIntent.

The interaction model likely needs more concrete (non-slotted) sample utterances for PlayAlbumIntent to improve disambiguation, or the PlayVideoIntent samples may be too greedy.

NLU test fixture: it-IT - "Di suonare album abbey road" (expected PlayAlbumIntent, got PlayVideoIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
