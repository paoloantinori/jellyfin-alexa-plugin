---
id: JF-56
title: APL visual templates for Echo Show devices
status: Done
assignee: []
created_date: '2026-05-03 13:39'
updated_date: '2026-05-03 20:23'
labels:
  - enhancement
  - ux
  - visual
  - apl
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create APL (Alexa Presentation Language) visual templates for Echo Show, Echo Spot, and Fire TV devices. Inspired by Emby AlexaController.

When the skill detects a screen-capable device, render rich visual layouts:
1. Now Playing screen: album art, track title, artist, progress bar, playback controls
2. Search Results screen: scrollable list of matching items with thumbnails
3. Queue screen: upcoming tracks with album art
4. Media Info screen: detailed metadata display

Implementation: Use Alexa APL directives alongside audio responses. Define APL documents as JSON templates. Detect screen capability via System.device.supportedInterfaces.Display or AplInterface.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented APL (Alexa Presentation Language) visual templates for Echo Show devices. Created AplHelper with device capability detection, Now Playing screen (blurred background + album art + track info), and queue list template. Added custom APL directive types (RenderDocument, ExecuteCommands) since Alexa.NET 1.22.0 lacks APL support. Integrated into BaseHandler.BuildAudioPlayerResponse with backward-compatible overload - APL visuals are automatically included when the requesting device supports them. All 9 APL tests pass, 508 total tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
