---
id: JF-8
title: Add localized responses for Alexa handlers
status: Done
assignee: []
created_date: '2026-04-29 21:25'
updated_date: '2026-04-30 15:32'
labels: []
milestone: m-1
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
All Alexa intent handlers return hardcoded English strings. The plugin supports en-US and it-IT locales but responds only in English.

**Current state:** Every handler has strings like "Sorry I could not find the media", "Media added to favorites list", etc. hardcoded in English.

**Implementation approach:**
1. Create a resource/resx file system for handler response strings
2. Support en-US and it-IT locales (matching existing interaction models)
3. Each handler should check `request.Locale` or `request.Request.Locale` from the Alexa request
4. Map locale to the appropriate string set
5. Fallback to en-US for unsupported locales

**Strings to localize (minimum):**
- Error messages: "Sorry I could not find...", "Something went wrong"
- Success messages: "Playing...", "Media added to favorites"
- Status messages: "No media currently playing", "Nothing is playing"
- Help/fallback: "I could not understand that", help text

**Note:** This is a medium-priority improvement. The interaction models already have Italian samples, so the user speaks Italian, but the plugin responds in English — this is a poor user experience.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Response string resource system created
- [ ] #2 en-US strings defined (default)
- [ ] #3 it-IT strings defined
- [ ] #4 All handlers use localized strings based on request locale
- [ ] #5 Fallback to en-US for unsupported locales
- [ ] #6 Unit tests verify correct locale selection
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented JSON-based localization with ResponseStrings class, en-US.json and it-IT.json embedded resources. All 16 handlers use localized strings via GetLocale() helper in BaseHandler. Supports string formatting for parameterized messages. Fallback chain: requested locale -> en-US -> key name. Fixed ManifestSkill namespace conflict. 12 new unit tests, all passing. Total test count: 130 passed, 1 skipped.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
