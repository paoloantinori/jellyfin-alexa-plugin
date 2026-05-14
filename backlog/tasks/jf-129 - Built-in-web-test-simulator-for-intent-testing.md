---
id: JF-129
title: Built-in web test simulator for intent testing
status: Done
assignee: []
created_date: '2026-05-12 04:45'
updated_date: '2026-05-12 13:15'
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
- [x] #1 Web endpoint exposes intent testing without requiring an actual Alexa device
- [x] #2 Simulator supports sending test intents with configurable slot values
- [x] #3 Returns full skill response including directives and speech output
- [x] #4 Authentication/authorization to prevent unauthorized access
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SimulatorController with 3 REST endpoints (Status, Intents, Intent execution). Routes through existing handler pipeline. Disabled by default via PluginConfiguration.SimulatorEnabled. Requires admin auth. 9 unit tests.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [x] #2 dotnet build passes with 0 errors
- [x] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [x] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->
