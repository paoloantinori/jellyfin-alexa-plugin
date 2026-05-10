---
id: JF-109
title: Extract GetUserById null guard into BaseHandler helper
status: Done
assignee: []
created_date: '2026-05-09 20:21'
updated_date: '2026-05-10 06:27'
labels:
  - refactoring
  - tech-debt
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The same 4-line null-check pattern is repeated across 20+ intent handlers:

```csharp
Jellyfin.Database.Implementations.Entities.User? jellyfinUser = _userManager.GetUserById(session.UserId);
if (jellyfinUser == null)
{
    return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", locale));
}
```

Extract this into a protected helper method on `BaseHandler`, e.g. `GetJellyfinUserOrError(session, locale, out var jellyfinUser)` that returns `null` and the error response, or a `(User? user, SkillResponse? error)` tuple. Replace all ~20 call sites with the single-line helper call.

This eliminates a class of copy-paste bugs where the wrong error string was used for item vs user lookups. A similar helper for `_libraryManager.GetItemById` null checks should also be considered (some handlers return `"UserNotFound"` for missing items, others `"MediaNotFound"`).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Extracted `ResolveJellyfinUser` helper into `BaseHandler` and migrated 24 handler call sites (21 standard + PlayNext + QueryArtistLibrary + FavoriteToggle).

Key decisions:
- Helper returns `(JellyfinUser? User, SkillResponse? Error)` tuple — stack-allocated, no overhead
- Used `JellyfinUser` type alias to avoid ambiguity with `Alexa.NET.Request.User`
- `MediaInfoIntentHandler` reverted to direct `GetUserById` — its method returns `string?` not `SkillResponse`, so the helper's error response would be discarded
- `PlaybackNearlyFinishedEventHandler` excluded — returns `Guid?` not `SkillResponse`
- `PlayFavorites`/`PlayLastAdded`/`SkillConnectionHandler` excluded — inline in `InternalItemsQuery`, no null check needed
- Non-async handlers (`YesIntentHandler`, `FavoriteToggleIntentHandler`) use `Task.FromResult(userError)`

Build: 0 errors. Tests: 983 passed.
<!-- SECTION:FINAL_SUMMARY:END -->
