---
id: JF-2.1
title: Implement empty PlayVideoIntentHandler
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-29 22:03'
labels: []
milestone: m-1
dependencies: []
parent_task_id: JF-2
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayVideoIntentHandler.cs` exists but has no implementation. The PlayVideoIntent is defined in both en-US and it-IT interaction models.

**Current state:** Handler class exists but is empty/stubbed.

**Implementation requirements:**
- Search Jellyfin library for video items matching the user's request
- Support searching by title, and potentially by genre/type
- Return an Alexa VideoApp directive with the video stream URL
- Handle cases: no results found, multiple results, authentication required
- Include proper error response speech for failures
- Log the search and playback attempt
- The VideoAppInterface.cs only defines the interface type — verify it supports launching video playback

**Reference:** Look at PlaySongIntentHandler and PlayAlbumIntentHandler for the pattern of searching and returning media. The video handler should follow the same pattern but use Alexa VideoApp directives instead of AudioPlayer directives.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PlayVideoIntentHandler fully implements HandleAsync()
- [ ] #2 Searches Jellyfin library for video content by title
- [ ] #3 Returns Alexa VideoApp directive with stream URL
- [ ] #4 Handles no-results case with appropriate speech response
- [ ] #5 Handles auth-required case
- [ ] #6 Includes unit tests for success, no-results, and error scenarios
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented PlayVideoIntentHandler: searches Jellyfin for movies/episodes by title, returns VideoApp.Launch directive for Echo Show. Created custom VideoAppLaunchDirective (IDirective), registered in controller. 11 unit tests cover canHandle, null/empty/whitespace title, no-results, directive content (source URL + metadata title), session queue, and multi-result selection.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
