---
id: JF-168
title: Add PlayBookIntent with dedicated handler and slot type
status: Done
assignee:
  - orchestrator
created_date: '2026-05-17 11:26'
updated_date: '2026-05-17 12:57'
labels:
  - feature
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/
  - Jellyfin.Plugin.AlexaSkill/Alexa/Locale/
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Books (AudioBook) are currently only discoverable via SearchMediaIntent using AMAZON.SearchQuery, which doesn't support entity resolution. This means:

1. Dynamic entities targeting `AudiobookTitle` slot type have NO effect — no intent references it
2. Alexa has no way to do structured resolution for book titles; it's free-text search
3. Users can't say "play audiobook [title]" with NLU-backed slot resolution

Need to add:
- `PlayBookIntent` with a `BookName` slot (or reuse `AudiobookTitle` custom slot type) in all 17 interaction models
- `PlayBookIntentHandler` that searches `BaseItemKind.AudioBook`, respects `BooksEnabled` feature flag
- Locale response strings for book-related responses across all 17 locales
- Wire into `DynamicEntityBuilder.BookIntents` set so dynamic entities inject book titles when the user enters book context

Depends on: JF-165 (proper AudioBook type check) for clean code patterns.

Content type note: Jellyfin classifies AudioBook as "books" (gated by `BooksEnabled`), not music.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation complete:
- Added PlayBook = "PlayBookIntent" to IntentNames.cs
- Created PlayBookIntentHandler.cs (searches BaseItemKind.AudioBook, respects BooksEnabled flag, uses fuzzy matching)
- Added IntentNames.PlayBook to BookIntents in DynamicEntityBuilder.cs
- Added PlayBookIntent with AudiobookTitle slot to all 17 interaction models
- Added 4 book-related response strings (ElicitBookName, NotFoundBook, NoContentInBook, SearchingBook) to all 17 locale files
- Created PlayBookIntentHandlerTests.cs with 7 tests
- All 1523 tests pass
<!-- SECTION:NOTES:END -->
