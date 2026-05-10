---
id: JF-103
title: >-
  Commit: controller unhandled-intent logging and artist fuzzy-match
  improvements
status: Done
assignee: []
created_date: '2026-05-09 06:59'
updated_date: '2026-05-09 07:06'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Currently deployed but uncommitted:

1. **AlexaSkillController**: Improved "Unhandled skill request" logging to include intent name and locale (was only logging RequestType="IntentRequest"). Changed response from `ResponseBuilder.Empty()` (silent) to `ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale))` (localized fallback). Uses `BaseHandler.GetLocalePublic()` for locale resolution.

2. **PlayArtistSongsIntentHandler**: Added fuzzy-match pre-check before disambiguation. When multiple artists match, uses `FuzzyMatch(musician, artists, a => a.Name)` — if a clear match exists, plays it directly; otherwise falls back to disambiguation dialog.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 git diff shows only the two intended changes
- [ ] #2 dotnet build passes
- [ ] #3 Changes are committed with descriptive message
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Committed in 89b4afc. Controller now logs intent name + locale for unhandled requests and responds with localized "CouldNotUnderstand" instead of empty. PlayArtistSongsIntentHandler uses FuzzyMatch before disambiguation. Simplify review passed clean.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
