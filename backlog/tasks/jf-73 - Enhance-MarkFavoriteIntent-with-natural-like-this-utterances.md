---
id: JF-73
title: Enhance MarkFavoriteIntent with natural "like this" utterances
status: Done
assignee: []
created_date: '2026-05-04 19:01'
updated_date: '2026-05-05 20:05'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MarkFavoriteIntent already exists but should be enhanced with more natural language utterances like "I like this song", "Thumbs up", "Add this to my favorites". The implementation is trivial — just adding additional sample utterances mapped to the existing intent. Pure UX improvement to make the skill feel more conversational.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Utterances 'I like this song', 'Thumbs up', 'Add this to my favorites' all trigger MarkFavoriteIntent
- [x] #2 Existing MarkFavoriteIntent behavior is unchanged — only new utterance mappings are added
- [x] #3 New utterances are added for all supported locales
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added natural MarkFavoriteIntent utterances for both locales. en-US: 6 new samples (I like this song, I like this, Thumbs up, Add this to my favorites, Save this to my favorites, Favorite this). it-IT: 3 new samples (Mi piace questa canzone, Salva nei preferiti, Metti tra i preferiti). No handler changes — pure NLU improvement. NLU fixtures updated. /simplify: no handler code changed, nothing to simplify.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
