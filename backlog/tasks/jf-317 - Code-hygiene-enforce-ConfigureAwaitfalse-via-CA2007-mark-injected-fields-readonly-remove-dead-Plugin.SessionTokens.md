---
id: JF-317
title: >-
  Code hygiene: enforce ConfigureAwait(false) via CA2007, mark injected fields
  readonly, remove dead Plugin.SessionTokens
status: Done
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-07-15 18:40'
labels:
  - code-quality
  - tech-debt
milestone: m-7
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Plugin.cs:123'
  - 'Jellyfin.Plugin.AlexaSkill/Diagnostics/JellyfinConnectivityChecker.cs:77'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs:98
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Bundle of low-risk code-hygiene items from the 2026-07-12 architecture review (all verified):

1. ~87 of ~408 awaits lack `ConfigureAwait(false)` (~21% non-compliant), concentrated in handlers/controllers (PlayArtistSongsIntentHandler, ConfigurationController, PlaySongIntentHandler, etc.). CLAUDE.md states the rule but it's enforced by hand. Fix: enable analyzer CA2007 (warning) and fix the flagged sites — a mechanical, self-verifying change instead of manual grep.

2. Injected DI fields across ~41 handlers are non-readonly and duplicated (e.g. NextIntentHandler.cs:21, PlayAlbumIntentHandler.cs:31-32). Mark all injected fields `readonly`. This also reduces the risk from the singleton-handler concurrency finding (a readonly field can't accidentally become per-request mutable state).

3. `Plugin.SessionTokens` (`Plugin.cs:123`) is a public non-thread-safe `Dictionary<string,string>` with ZERO references anywhere. Delete it (removes a latent concurrency hazard).

4. `Diagnostics/JellyfinConnectivityChecker.cs:77` blocks a threadpool thread with `_semaphore.Wait()`. Switch to `await WaitAsync`.

5. `DynamicEntities/DynamicEntitiesInterceptor.cs:98` wraps synchronous directive building in `Task.Run` on the response path — a needless threadpool hop. Call synchronously or make genuinely async.

Each is independently small; group them as one cleanup PR. Warnings-as-errors is on, so CA2007 will surface all sites.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CA2007 is enabled and all handler/controller awaits comply (build clean under warnings-as-errors)
- [ ] #2 All injected DI fields in handlers are readonly
- [ ] #3 Plugin.SessionTokens is removed (confirmed zero references before deletion)
- [ ] #4 JellyfinConnectivityChecker uses await WaitAsync instead of blocking Wait()
- [ ] #5 DynamicEntitiesInterceptor no longer wraps sync work in Task.Run on the response path
- [ ] #6 Full test suite passes
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Split per user decision 2026-07-15. 3 of 5 parts shipped in eea9cea and are closed here: (3) Plugin.SessionTokens removed (zero references, was a non-thread-safe Dictionary); (4) JellyfinConnectivityChecker now uses await WaitAsync() instead of blocking Wait(); (5) DynamicEntitiesInterceptor no longer wraps sync directive building in Task.Run on the response path. The remaining 2 parts — (1) CA2007 analyzer enablement + ~87 ConfigureAwait(false) fixes, and (2) readonly sweep across ~41 injected handler fields — are split into a dedicated task (CA2007 + readonly sweep) as substantial codebase-wide mechanical work.
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
