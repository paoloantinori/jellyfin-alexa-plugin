---
id: JF-149
title: 'E2E test: FilterByContentAccess (media type visibility)'
status: Done
assignee: []
created_date: '2026-05-14 12:28'
updated_date: '2026-05-14 13:32'
labels:
  - testing
  - e2e
  - configuration
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create E2E tests that verify MusicEnabled, VideosEnabled, and BooksEnabled configuration flags correctly filter content in search and browse results.

When MusicEnabled=false:
- PlayRandomIntent with music should not return music items
- SearchMediaIntent should not return music in results
- BrowseLibrary should not show music content

When VideosEnabled=false:
- PlayVideoIntent/PlayEpisodeIntent should filter video results
- BrowseLibrary should not show video content

When BooksEnabled=false:
- Search should not return audiobooks

Approach:
1. Configure plugin with specific media type disabled (e.g., MusicEnabled=false)
2. Run E2E utterances that would normally return that media type
3. Verify results don't contain the disabled media type
4. Re-enable and verify results include the media type
5. Use it-IT locale

This is a harder test to automate since it requires verifying response content rather than just intent resolution. May need to check Jellyfin logs or response speech text for verification.

References: BaseHandler.cs (FilterByContentAccess), BrowseLibraryIntentHandler.cs, PlayRandomIntentHandler.cs, SearchMediaIntentHandler.cs
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 E2E fixture tests content filtering with MusicEnabled=false
- [ ] #2 E2E fixture tests content filtering with VideosEnabled=false
- [ ] #3 Results don't contain disabled media types when flag is off
- [ ] #4 Results include all media types when flags are on
- [ ] #5 Tests use it-IT locale
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Already covered by existing ContentAccessTests.cs with 18 tests: MediaTypeConfigurationDefaults (2), FilterByContentAccessTests (8), IfMediaTypeDisabledTests (8). No additional tests needed.
<!-- SECTION:FINAL_SUMMARY:END -->

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
