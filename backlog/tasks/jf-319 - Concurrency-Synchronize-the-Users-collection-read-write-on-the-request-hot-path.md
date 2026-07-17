---
id: JF-319
title: >-
  Concurrency: Synchronize the Users collection read/write on the request hot
  path
status: To Do
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-13 20:17'
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
