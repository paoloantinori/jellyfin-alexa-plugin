---
id: JF-3
title: Implement empty PlayChannelIntentHandler
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:10'
labels: []
milestone: m-1
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayChannelIntentHandler.cs` exists but has no implementation. The PlayChannelIntent is defined in both en-US and it-IT interaction models.

**Current state:** Handler class exists but is empty/stubbed.

**Implementation requirements:**
- Search Jellyfin for live TV channels by name
- Use ILibraryManager or IChannelManager to find channels
- Return AudioPlayer directive with the channel stream URL
- Handle cases: channel not found, no channels configured, auth required
- Include proper error response speech
- Log the channel lookup and playback attempt

**Reference:** Look at PlaySongIntentHandler for the pattern. Channel playback may require different Jellyfin API calls since channels are a separate content type. Check if IChannelManager or similar interface is available in the Jellyfin SDK.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayChannelIntentHandler fully implements HandleAsync()
- [ ] #2 Searches Jellyfin for live TV channels by name
- [ ] #3 Returns AudioPlayer directive with channel stream URL
- [ ] #4 Handles no-results and no-channels-configured cases
- [ ] #5 Handles auth-required case
- [ ] #6 Includes unit tests for success, no-results, and error scenarios
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayChannelIntentHandler: searches Jellyfin for LiveTvChannel by name, returns AudioPlayerPlay directive. Uses same null-safe slot access pattern as PlayVideoIntentHandler. Registered in controller. 10 unit tests cover all edge cases.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
