---
id: JF-153
title: 'UX: Accordion triangle too small and title on separate line'
status: Done
assignee:
  - claude
created_date: '2026-05-14 15:07'
updated_date: '2026-05-14 16:19'
labels:
  - ux
  - configuration
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The accordion sections in the plugin configuration page have two visual issues:

1. **Expand/collapse triangle is too small** — the clickable arrow indicator is tiny and hard to tap/click. Should be larger for better usability.
2. **Section title wraps to next line** — the accordion title (e.g. "Feature Flags", "Content Type Visibility") appears on the line below the triangle instead of inline beside it. This looks broken and is confusing — users don't immediately associate the title with the clickable triangle above it.

**Expected**: Triangle and title on the same line, with a larger clickable triangle indicator.

**Actual**: Triangle on one line, title on the next line below. Triangle is very small.

File to fix: `Jellyfin.Plugin.AlexaSkill/Configuration/config.html` — the accordion CSS/HTML layout.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed accordion CSS: flexbox layout for inline triangle+title, larger CSS border-based arrows (16px) replacing tiny Unicode chars, smooth rotate transition, removed default details marker.
<!-- SECTION:FINAL_SUMMARY:END -->
