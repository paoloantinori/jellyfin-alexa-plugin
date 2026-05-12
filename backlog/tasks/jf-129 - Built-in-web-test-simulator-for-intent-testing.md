---
id: JF-129
title: Built-in web test simulator for intent testing
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - developer-experience
  - testing
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add a built-in web test simulator endpoint for testing intents without an actual Alexa device. This would speed up development iteration compared to our current SMAPI-only testing approach.

Inspired by Music Assistant's /simulator endpoint which allows testing intents locally with a web UI, including a simulator-specific bypass for request signature verification.

Implementation: Add an HTTP endpoint (optionally enabled via config) that accepts intent requests in Alexa-compatible format and routes them through the skill's handler pipeline. Could be a Jellyfin API endpoint or a separate dev-tools page.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Web endpoint exposes intent testing without requiring an actual Alexa device
- [ ] #2 Simulator supports sending test intents with configurable slot values
- [ ] #3 Returns full skill response including directives and speech output
- [ ] #4 Authentication/authorization to prevent unauthorized access
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
