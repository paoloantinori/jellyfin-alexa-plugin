---
id: JF-60
title: Voice-based user identification
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 20:53'
labels:
  - enhancement
  - user-management
  - voice-interaction
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement voice-based user identification that maps Alexa's voice profile to a specific Jellyfin user account. Inspired by Emby AlexaController.

Support utterances like:
- "Learn my voice" / "Recognize me"
- "Who am I?" / "Which account is this?"

Implementation:
1. Use Alexa's speaker recognition (personId in request context) when voice profiles are enabled
2. Store mapping of Alexa voice ID → Jellyfin user ID in plugin configuration
3. When a recognized voice issues a command, automatically use the mapped Jellyfin user's library and preferences
4. When an unrecognized voice is detected, either default to a primary user or ask "Who is speaking?"
5. Enforce per-user library access - each user only sees their own media

This is especially useful for families with shared Echo devices but individual Jellyfin accounts.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented voice-based user identification using Alexa speaker recognition. Added AlexaPersonId property to User entity, GetUserByPersonId to PluginConfiguration, and personId-first auth flow in both controller and BaseHandler. Created LearnMyVoiceIntentHandler (links voice profile to account with collision guard) and WhoAmIIntentHandler (reports current identity). Updated controller to allow requests with personId but no AccessToken. Added interaction model entries for en-US and it-IT. 9 tests passing, 518 total suite passing.
<!-- SECTION:FINAL_SUMMARY:END -->
