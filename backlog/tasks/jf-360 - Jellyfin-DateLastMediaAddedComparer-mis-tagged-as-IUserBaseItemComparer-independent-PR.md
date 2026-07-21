---
id: JF-360
title: >-
  Jellyfin: DateLastMediaAddedComparer mis-tagged as IUserBaseItemComparer
  (independent PR)
status: To Do
assignee: []
created_date: '2026-07-21 05:15'
updated_date: '2026-07-21 08:25'
labels:
  - upstream-jellyfin
  - sorting
  - independent-pr
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
INDEPENDENT BUG from the GetUserData NRE investigation (JF-360 / separate PR).

Emby.Server.Implementations/Sorting/DateLastMediaAddedComparer declares `: IUserBaseItemComparer` and exposes User/UserManager/UserDataManager properties, but its Compare -> GetDate is `static` and reads only `folder.DateLastMediaAdded`. It NEVER references User/UserData. So it is statically mis-tagged as a user-dependent comparer.

Consequence: in LibraryManager.GetComparer, the null-user guard (added in the NRE fix) skips DateLastContentAdded sorting for every anonymous /Items query, even though the comparer would work perfectly with no user. Before the NRE fix, it crashed; after, it's silently disabled for anonymous queries. Either way, DateLastContentAdded sort is broken for anonymous requests because of the bogus interface tag.

FIX: drop IUserBaseItemComparer from DateLastMediaAddedComparer (implement only IBaseItemComparer), remove the unused User/UserManager/UserDataManager properties. Then GetComparer's null-user check applies only to the 5 comparers that genuinely need a user (PlayCount, DatePlayed, IsFavoriteOrLiked, IsPlayed, IsUnplayed).

VERIFIED via source read: GetDate is static, no User reference. Verified during /simplify altitude review of the NRE fix (agent finding F2).

Worktree: separate git worktree off the Jellyfin repo, independent PR. Do NOT bundle into the GetUserData NRE PR (JF-360's sibling).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DECISION (2026-07-21): folded into Bug 1's PRs (#17394 release-10.11.z, #17395 master), NOT a separate PR/issue. Reason: the DateLastMediaAddedComparer mis-tag is a gap in Bug 1's GetComparer SortName-fallback (that fallback wrongly degrades DateLastContentAdded sort for anonymous queries), not an independent bug. Proven via TDD on the release branch: with Bug 1's fallback present + comparer still tagged, the JF-360 test RED (date sort crashes via SortName/CreateSortName NRE); after un-tag, GREEN (date sort works). No separate GitHub issue filed — covered by #17393.
<!-- SECTION:NOTES:END -->
