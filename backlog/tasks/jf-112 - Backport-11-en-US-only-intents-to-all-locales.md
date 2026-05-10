---
id: JF-112
title: Backport 11 en-US-only intents to all locales
status: To Do
assignee: []
created_date: '2026-05-09 20:30'
labels:
  - nlu
  - multi-locale
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
These intents exist only in en-US and need localized utterances added to the other 11 locale models:

1. AddToQueueIntent (slots: song, musician)
2. ClearQueueIntent (no slots)
3. ListQueueIntent (no slots)
4. PlayNextIntent (slots: song, musician)
5. PlayRadioIntent (no slots)
6. TurnRadioOnIntent (no slots)
7. TurnRadioOffIntent (no slots)
8. LearnMyVoiceIntent (no slots)
9. QueryArtistLibraryIntent (slots: musician, query_type — requires LibraryQueryType slot type)
10. WhoAmIIntent (no slots)
11. PlayByDecadeIntent (slots: decade, genre — requires Decade slot type)

For each intent, add locale-appropriate sample utterances to all 12 model_*.json files. Also add the required slot types (Decade, LibraryQueryType) where missing. Use en-US as the reference for utterance patterns, translating to each locale's language.

Discovered in JF-98 audit.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
