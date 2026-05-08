---
id: JF-27
title: Write comprehensive installation and configuration tutorial in README.md
status: Done
assignee: []
created_date: '2026-05-03 06:34'
updated_date: '2026-05-03 06:47'
labels:
  - documentation
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create a complete, step-by-step README.md covering:

1. **Prerequisites** - Jellyfin 10.11.x, public HTTPS URL, Amazon Developer account
2. **Installation** - Installing from Jellyfin plugin catalog
3. **Amazon Developer setup** - Creating a Security Profile for LWA (Login with Amazon), getting Client ID and Client Secret, with direct links to https://developer.amazon.com/alexa/console/ask and https://developer.amazon.com/settings/console/securityprofile
4. **Plugin Configuration** - Server address, LWA credentials, Account Linking Client ID
5. **LWA Authorization** - Clicking Authorize, completing the Amazon login flow
6. **Account Linking** - Linking in the Alexa app
7. **Testing** - Using the Alexa simulator and voice commands
8. **Supported Voice Commands** - List of intents and example phrases per locale
9. **Troubleshooting** - Common issues (expired tokens, interaction model build failures, endpoint errors)

Include direct links to all third-party services:
- Amazon Developer Console: https://developer.amazon.com/alexa/console/ask
- Security Profile management: https://developer.amazon.com/settings/console/securityprofile
- LWA documentation: https://developer.amazon.com/docs/login-with-amazon/documentation-overview.html

Target audience: Jellyfin users who are not developers. Keep it clear and accessible.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Complete README rewrite covering: prerequisites, 3 installation methods, step-by-step Amazon Developer Security Profile setup with direct links, plugin configuration, LWA authorization flow with status indicators, account linking walkthrough, testing guide (simulator + Echo), voice commands organized by category (playback, control, favorites, media info), 12 supported locales, and troubleshooting for 6 common issues.
<!-- SECTION:FINAL_SUMMARY:END -->
