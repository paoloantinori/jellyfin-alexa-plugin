---
id: JF-54
title: Dialog delegation for multi-slot intents
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 20:03'
labels:
  - enhancement
  - ux
  - alexa-sdk
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement Alexa dialog delegation for complex multi-slot intents using addDelegateDirective. Inspired by official ASK SDK documentation.

Currently the plugin handles multi-slot intents (e.g., "play {song} by {artist}") with manual slot extraction. Dialog delegation lets Alexa automatically collect missing slots based on the interaction model definition.

Implementation:
1. Mark intents with required slots in the interaction model with dialog.intents[].confirmationStatus and slots[].elicitationPrompts
2. For IN_PROGRESS dialog state, validate partial input and delegate back to Alexa for remaining slots
3. Reduces custom handler code for slot collection
4. Apply first to PlaySongIntent (song + optional artist), PlayAlbumIntent (album + optional artist), PlayEpisodeIntent (series + season + episode)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented Alexa dialog delegation for PlayEpisodeIntent, PlaySongIntent, and PlayAlbumIntent. When users invoke these intents without providing all required slots, Alexa now automatically asks follow-up questions (e.g., "Which series?", "What song?") instead of returning an error.

Changes:
- Added `dialog` and `prompts` sections to model_en-US.json and model_it-IT.json with slot elicitation definitions
- Added `DelegateToDialog()` helper in BaseHandler that returns a Dialog.Delegate directive
- Added `DialogStates` constants (Started, InProgress, Completed) in IntentNames.cs
- Updated PlayEpisodeIntentHandler, PlaySongIntentHandler, PlayAlbumIntentHandler to check DialogState and delegate when not COMPLETED
- Added 7 new tests for dialog delegation behavior
- Updated 2 existing tests to set DialogState = "COMPLETED"
<!-- SECTION:FINAL_SUMMARY:END -->
