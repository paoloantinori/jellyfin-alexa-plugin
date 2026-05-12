---
id: JF-127
title: Locale fallback chain for missing translations
status: To Do
assignee: []
created_date: '2026-05-12 04:45'
labels:
  - enhancement
  - localization
  - reliability
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement a locale fallback chain: try exact locale first (e.g., es-MX), then fall back to the language root (es), then to en-US as ultimate fallback. Currently missing locale keys cause runtime exceptions.

Inspired by AskPlex's language interceptor that tries specific locale then falls back to generic language. Their contributor giogua added this to make the skill work across locale variants without needing complete translations for every one.

Implementation: Modify `ResponseStrings.Get()` to implement a fallback chain before throwing. Log warnings for missing keys at the specific locale level.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Locale lookup tries exact match first (e.g., es-MX), then falls back to language root (es), then en-US
- [ ] #2 No runtime exceptions for missing locale keys
- [ ] #3 Fallback chain logged at debug level for troubleshooting
- [ ] #4 All existing locale files verified to have complete coverage
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
