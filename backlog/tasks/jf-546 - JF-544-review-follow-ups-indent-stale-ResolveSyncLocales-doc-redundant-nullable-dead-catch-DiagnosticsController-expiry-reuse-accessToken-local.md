---
id: JF-546
title: >-
  JF-544 review follow-ups: indent, stale ResolveSyncLocales doc, redundant
  #nullable, dead catch, DiagnosticsController expiry reuse, accessToken local
status: To Do
assignee: []
created_date: '2026-09-12 07:54'
labels: []
dependencies: []
references:
  - commit a7a55961
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Minor cleanups surfaced by the /code-review high pass of commit a7a55961 (JF-544) that fell below the review's 10-finding cap; filed so they are tracked, not lost.

1. LibrarySyncService.cs lines 30-36: the new SyncTokenBudgetMinutes const block (doc comment + declaration + blank line) is indented 8 spaces while every other class member uses 4 spaces (verified with cat -A). Reindent to 4.
2. LibrarySyncService.cs line 426: the ResolveSyncLocalesAsync doc says "Empty: it-IT only (default)" but the code default of CatalogSyncLocales is "*" (PluginConfiguration.cs:107, all active locales); the empty string is it-IT only. Fix the doc line.
3. SmapiTokenRefresher.cs line 1: "#nullable enable" is redundant (csproj sets Nullable enable project-wide; no other file in Lwa/ carries it). Remove.
4. TokenRefreshTask.cs lines 97-114: the try/catch(Exception) around SmapiTokenRefresher.RefreshAsync is dead defensive code given the never-throws contract (its own comment says so). Either delete the catch or keep it and add the contract-pinning test from JF-545 acceptance #5; do not leave the comment and the code shape contradicting each other.
5. DiagnosticsController.cs lines ~49-55 and ~188-191 hand-roll the SmapiDeviceToken expiry test that SmapiTokenRefresher.RemainingLifetime now encapsulates (RemainingLifetime(u) < TimeSpan.Zero, with the unknown-expiry semantic difference noted). Route them through RemainingLifetime so the diagnostics surface and the refresh logic cannot disagree after future changes.
6. LibrarySyncService.cs line 104: the accessToken local now feeds only the ResolveSyncLocalesAsync call at line 110; its placement beside the run-length vendorId/skillId snapshots invites a future edit to reuse it inside RunLegAsync and reintroduce the stale-snapshot bug JF-544 fixed. Inline the re-read at the call site.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SyncTokenBudgetMinutes block uses 4-space member indentation matching the file
- [ ] #2 ResolveSyncLocalesAsync doc states the actual default ('*' = all active locales; empty = it-IT only)
- [ ] #3 Redundant #nullable enable removed from SmapiTokenRefresher.cs or a rationale comment added
- [ ] #4 TokenRefreshTask's catch either removed or justified by a pinned never-throws test
- [ ] #5 DiagnosticsController expiry checks call SmapiTokenRefresher.RemainingLifetime (semantics change for unknown-expiry documented in the diff)
- [ ] #6 No behavior change beyond the doc/style/dedup edits; full unit suite passes
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

7. (from the final /simplify pass, 2026-09-12) The test project now carries FOUR private capturing-ILoggerProvider copies (SkillResponseLoggingTests, VideoAudioControllerTests, StructuredLoggingTests, and the JF-544 LibrarySyncServiceSeriesTests one, which already diverges as string-only vs (LogLevel, Message) tuples): promote one shared internal helper in TestHelpers.cs and migrate the four. Also note item 1 (indent) and item 4 (dead catch) were RESOLVED by the same pass; item 6 (accessToken local) was already moot after 8674ad84 - only a stale comment remained, also removed.

PROGRESS (2026-09-12, commits 5513efc7 + 39b26f56): item 1 (indent) resolved in the JF-544 /simplify pass; the dead catch in TokenRefreshTask resolved in the same pass; the DiagnosticsController expiry-arithmetic item RESOLVED (adopts SmapiTokenRefresher.RemainingLifetime, commit 39b26f56); the shared TestCaptureLogger helper landed (5513efc7, first of four copies migrated). REMAINING: migrating the other two/three logger-capture copies (SkillResponseLoggingTests, VideoAudioControllerTests), the stale ResolveSyncLocales default doc, the redundant #nullable enable, the misplaced accessToken local (moot after 8674ad84), and the log-level reroute decision (JF-547 item 6).

CLOSE-OUT (2026-09-12, commits 5513efc7/39b26f56/005ea14e): item 1 (indent) resolved in the JF-544 /simplify pass; item 3 (redundant #nullable) removed in 005ea14e; item 4 (dead catch) removed in the same /simplify pass; item 5 (DiagnosticsController expiry reuse) resolved in 39b26f56 (RemainingLifetime); item 6 (accessToken local) was moot after 8674ad84 (the local was already inlined; the stale comment was removed); item 2 (ResolveSyncLocales doc) corrected in 005ea14e (the '*' default + JF-543 exclusion). Item 7 (the four logger-capture copies) resolved: TestCaptureLogger landed and all copies migrated (1ff8b159). All items closed; task closable.