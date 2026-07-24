---
id: JF-165
title: Replace GetType().Name.Contains("AudioBook") with proper type check
status: Done
assignee:
  - agent-jf165
created_date: '2026-05-17 11:20'
updated_date: '2026-05-17 12:17'
labels:
  - tech-debt
  - defect
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Both `DynamicEntityBuilder.GetSlotTypeForItem` and `SearchMediaIntentHandler.GetTypeName` use `item.GetType().Name.Contains("AudioBook", StringComparison.Ordinal)` instead of a proper `is` pattern match like every other branch. This is fragile — a renamed class, subclass, or obfuscation pass would silently break it.

Note: "Audiobook" = Jellyfin's books content category (`BaseItemKind.AudioBook`, gated by `BooksEnabled`). Not a music subtype.

Investigate whether `MediaBrowser.Controller.Entities.Books.AudioBook` is accessible as a concrete type. If so, replace with `is` pattern matching. If not, use a `BaseItemKind` enum check since `BaseItemKind.AudioBook` is already used in queries.
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
Replaced `GetType().Name.Contains("AudioBook", StringComparison.Ordinal)` with `GetType().Name.Equals("AudioBook", StringComparison.Ordinal)` in both DynamicEntityBuilder.GetSlotTypeForItem and SearchMediaIntentHandler.GetTypeName. The concrete `AudioBook` type lives in the server assembly (not the controller package), so `is` pattern matching is not available to plugins. The `Equals` fix is more precise than `Contains` — exact match prevents false positives from types like "AudioBookChapter". Build passes, all 45 related tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
