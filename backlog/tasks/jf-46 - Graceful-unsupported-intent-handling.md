---
id: JF-46
title: Graceful unsupported intent handling
status: Done
assignee: []
created_date: '2026-05-03 13:37'
updated_date: '2026-05-03 15:21'
labels:
  - enhancement
  - resilience
  - certification
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add graceful handling for all built-in Amazon intents that the skill doesn't fully support. Required for Alexa skill certification.

Currently, if Alexa routes a built-in intent (e.g., AMAZON.RepeatIntent, AMAZON.StartOverIntent) that doesn't have a dedicated handler, the skill may fail or return an error. The official Amazon audio player sample demonstrates an UnsupportedAudioIntentHandler pattern that catches these and responds with "Sorry, I can't support that yet" rather than crashing.

Implementation: Add catch-all handlers for any unhandled built-in intents that return a polite, localized "not supported" message with a reprompt. This is a certification requirement per Amazon's guidelines.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Extended FallbackIntentHandler to catch all unsupported AMAZON.* built-in intents. Returns distinct "UnsupportedIntent" locale string for unsupported built-ins vs "CouldNotUnderstand" for fallback. Added locale strings for 12 languages. 9 unit tests passing.
<!-- SECTION:FINAL_SUMMARY:END -->
