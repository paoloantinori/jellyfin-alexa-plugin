---
id: JF-319
title: >-
  Concurrency: Synchronize the Users collection read/write on the request hot
  path
status: Done
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-27 08:54'
labels:
  - concurrency
  - reliability
milestone: m-8
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs:186'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:285'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`PluginConfiguration.Users` is a plain `Collection<User>` (`PluginConfiguration.cs:186`) iterated with `foreach` via `GetUserById`/`GetUserByPersonId` on EVERY Alexa request (`AlexaSkillController.cs:340,348`; `BaseHandler.cs:285,296`), while `AddUser`/`DeleteUser` do `Users.Add/Remove` (`PluginConfiguration.cs:399,454`) from the config controller with no synchronization. A config edit concurrent with an in-flight request can throw `InvalidOperationException: Collection was modified during enumeration` or produce a torn read. Verified 2026-07-12. Low probability today (single admin, rare edits) but a real data race and cheap to close.

Fix: snapshot the collection to an immutable array on read, or guard reads/writes with a lock / swap-in-place of an immutable list. Keep serialization behavior intact (Jellyfin serializes this to config XML).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Concurrent reads of Users during an Add/Remove no longer risk InvalidOperationException or torn reads
- [ ] #2 GetUserById/GetUserByPersonId read from a consistent snapshot
- [ ] #3 Add/Delete user still persists correctly to plugin config (serialization unchanged)
- [ ] #4 A concurrency test (or documented reasoning) demonstrates the read path is safe under concurrent mutation
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
IMPLEMENTED 2026-07-27 (committed, deployed, live-verified).

Fix: PluginConfiguration.Users converted from a plain auto-property Collection<User> to COPY-ON-WRITE with a CAS retry loop. The backing _users field is swapped atomically; AddUser/DeleteUser build a new collection and commit via Interlocked.CompareExchange(ref _users, next, snapshot), retrying if another writer swapped in between read and commit. Every reader (GetUserById/GetUserByPersonId, and the ~15 callers that foreach/index/LINQ over config.Users) sees one consistent, never-mutated-in-place snapshot. This eliminates the InvalidOperationException: Collection was modified race and torn reads on the request hot path without requiring callers to take a lock.

AC #1 (no InvalidOperationException/torn read under concurrent Add/Remove): DONE. Concurrency test Users_ConcurrentReadWrite_NoInvalidOperationExceptionOrTornRead proves it - and FAILS against the pre-fix code (verified by reverting: throws the exact race).
AC #2 (GetUserById/GetUserByPersonId read a consistent snapshot): DONE - they foreach over the immutable-per-read _users reference.
AC #3 (Add/Delete persists correctly): DONE - XmlSerializer round-trip preserved (setter swaps in the deserialized instance); existing AddUser/DeleteUser invariants (duplicate throws, bool return) preserved.
AC #4 (concurrency test demonstrates read safety): DONE - plus a second test AddUser_ConcurrentWriters_NoUserLost that proves the CAS loop closes the writer-writer lost-update hazard (also FAILS against the pre-CAS code).

CODE-REVIEW HIGH (5 agents): one real finding - the lost-update hazard across concurrent writers (silent user loss). Fixed with the CAS retry loop per maintainer decision. Doc comment scoped to production write paths (tests bypass via config.Users.Add directly; the invariant holds for production code which has no in-place mutation). /simplify (4 agents) clean. Release build 0 warnings, 2636 tests green.

LIVE-VERIFIED on minix: deployed to active 0.11.2.0 (identifier present), config survived (1 user), PlayArtistSongs smoke test resolved the user + played Pink Floyd (hot-path GetUserById read works under COW).

Note: the separate caller-side check-then-act race (ConfigurationController.cs:1062 comment, GetUserById-then-AddUser) is out of scope and already tolerated by the codebase; the CAS loop closes the lost-update window INSIDE AddUser/DeleteUser, not that caller-side race.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
