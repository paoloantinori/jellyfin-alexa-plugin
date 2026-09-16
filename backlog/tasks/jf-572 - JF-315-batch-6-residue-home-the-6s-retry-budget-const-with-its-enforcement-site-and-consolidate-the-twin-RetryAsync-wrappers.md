---
id: JF-572
title: >-
  JF-315 batch-6 residue: home the 6s retry-budget const with its enforcement
  site and consolidate the twin RetryAsync wrappers
status: In Progress
assignee: []
created_date: '2026-09-15 22:58'
updated_date: '2026-09-16 15:52'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/SearchService.cs
  - Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/CrossMediaFallback.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/RadioTrackSource.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/TvNextUpService.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
JF-315 batch 6 extracted SearchService and left two deliberate-but-temporary seams that this task consolidates (both flagged by the batch-6 /simplify altitude review, deferred there to keep the extraction a pure verbatim move):

1. THE TWIN RetryAsync WRAPPERS. BaseHandler.RetryAsync (Alexa/Handler/BaseHandler.cs, ~line 1267) and SearchService.RetryAsync (Alexa/Util/SearchService.cs, tail) are now identical private wrappers delegating to RetryHelper.ExecuteWithRetryAsync with the same 6s budget. This mirrors JF-570 (the twin SortByRating overloads split across BaseHandler and ResumeMath by the same batch-2 verbatim-move discipline). Consolidation direction: the shared wrapper belongs with the budget's true owner (see item 2) or on RetryHelper itself, with BaseHandler and SearchService keeping only a thin call.

   SCOPE EXTENSION (JF-315 batch 7, 2026-09-16, from the batch-7 /simplify review): batch 7's CrossMediaFallback extraction added a THIRD identical RetryAsync twin (Alexa/Handler/CrossMediaFallback.cs, tail) plus a private Shuffle twin (verbatim Fisher-Yates copy of BaseHandler.Shuffle, needed by the moved BuildArtistSongsResponseAsync; its consolidation belongs with the cluster-H Shuffle family rather than with the RetryAsync work). The consolidation above must cover all THREE RetryAsync wrappers; the Shuffle twin rides the cluster-H extraction per its own doc comment.

   SCOPE EXTENSION (JF-315 batch 8, 2026-09-16): batch 8's AlbumPlayService extraction added a FOURTH identical RetryAsync twin (Alexa/Handler/AlbumPlayService.cs, tail); the consolidation must cover all FOUR wrappers (BaseHandler, SearchService, CrossMediaFallback, AlbumPlayService). Batch 8 also DELETED BaseHandler.IsMusicEnabled (the move orphaned it: its only caller, the album cascade's music gate, moved to AlbumPlayService, which now carries its own twin beside CrossMediaFallback's), so the IsMusicEnabled pair to align at consolidation time is CrossMediaFallback + AlbumPlayService, both expression-identical JF-467 live reads.

2. THE BUDGET CONST'S TRUE HOME. AlexaRequestTimeoutMs is a private const on BaseHandler (6000, documented as matching AlexaSkillController's CancellationTokenSource(TimeSpan.FromSeconds(6)) at Controller/AlexaSkillController.cs ~line 399). Batch 6 passed it into SearchService at composition (the PlaybackLaunchBuilder delegate-seam precedent) so Util holds no Handler-layer reference, but the linkage is still by convention: the controller hardcodes its own 6 and reads no const. Batch 7 composition-passes the same const into CrossMediaFallback the same way. The review's recommendation: home the budget as a public const on RetryHelper (whose test suite already names the budget invariant, see RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget), have RetryHelper.ExecuteWithRetryAsync's timeoutMs default to it, and make AlexaSkillController's CTS consume it; BaseHandler's const then aliases or deletes.

Context you need: the budget is behavior-load-bearing (it is the mechanism that keeps slow play-path queries inside Alexa's ~8s response window, see the repo CLAUDE.md coverage caveat), so any change must keep BOTH the value (6000) and the single-source property. The suite is the safety net (~3939 facts both TFMs; never dotnet test --no-build after changes).
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SCOPE EXTENSION + RESOLUTION (JF-315 batch 10, 2026-09-16): batch 10's RadioTrackSource extraction added a FIFTH identical RetryAsync twin (Alexa/Util/RadioTrackSource.cs, tail); the consolidation must now cover all FIVE wrappers (BaseHandler, SearchService, CrossMediaFallback, AlbumPlayService, RadioTrackSource). RESOLVED by the same batch, no longer in this task's scope: the CrossMediaFallback private Shuffle TWIN is DELETED. The cluster-H Shuffle family (Shuffle/ShuffleCopy/ShuffleAndCap) moved to the static Alexa/Util/Shuffler.cs and CrossMediaFallback's BuildArtistSongsResponseAsync now calls Shuffler.Shuffle (the batch-7 scope-extension note's own pointer to the cluster-H extraction, executed as planned).

BATCH-10 /simplify LEDGER ADDITION (2026-09-16): the shared ResumeMath.SortByRating 4-tuple carries a write-only UserItemData? Data element (pre-existing in SortAndFindResumeIndex since batch 2; FavoritesAndRatingsFirst's JF-570 consolidation now builds the same shape). No reader dereferences .Data anywhere (grep-verified by the altitude review); each loop holds the local it consumes. Dropping the element would be a behavior-neutral 3-line cleanup inside the two methods when next touched; noted here rather than done in the verbatim-move batch.

BATCH-10 /simplify LEDGER ADDITION (2026-09-16): DeviceQueueManager keeps TWO private Fisher-Yates copies (FisherYates(List<string>, Random), rng-injectable for its deterministic tests via SetShuffledQueue's rng parameter, plus ShuffleRemaining's inline tail shuffle, which deliberately pins Random.Shared). They are NOT twins of Shuffler.Shuffle (different contracts) and predate the decomposition; left in place, documented in the Shuffler class doc.

SCOPE EXTENSION (JF-315 batch 11, 2026-09-16): batch 11's TvNextUpService extraction added a SIXTH identical RetryAsync twin (Alexa/Handler/TvNextUpService.cs, tail); the consolidation must now cover all SIX wrappers (BaseHandler, SearchService, CrossMediaFallback, AlbumPlayService, RadioTrackSource, TvNextUpService). Batch 11 is the FINAL extraction batch of JF-315, so the twin count is now final unless a new collaborator lands before JF-572 executes.

EXECUTED (2026-09-16, implementation left uncommitted for the orchestrator's gates):

1. CONST HOME. `public const int AlexaRequestTimeoutMs = 6000` now lives on RetryHelper (Alexa/RetryHelper.cs), documented as the single source of the shared request budget and the mechanism keeping slow play-path queries inside Alexa's ~8s window (JF-358/JF-359; Sync_AlwaysTransient_StopsWithinTimeoutBudget pins the invariant). Both ExecuteWithRetryAsync overloads default `timeoutMs` to it, and a NEW shared entry point `RetryHelper.ExecuteWithRequestBudgetAsync<T>(operation, logger, operationName, timeoutMs = AlexaRequestTimeoutMs, cancellationToken)` carries the budget call shape once (non-nullable int budget: a caller cannot silently drop it; the JF-576 remainder override passes an explicit value). BaseHandler's private const is DELETED (no alias): its 5 composition sites and the SessionLookupTimeoutMs doc cref now read `RetryHelper.AlexaRequestTimeoutMs` directly. AlexaSkillController's request CTS is `new CancellationTokenSource(TimeSpan.FromMilliseconds(RetryHelper.AlexaRequestTimeoutMs))` (was FromSeconds(6), line ~399).

2. BEHAVIOR PRESERVATION AT THE NULL SEAM. Defaulting `timeoutMs` from null to 6000 would have silently put the three background callers on the request budget; each now passes an EXPLICIT `timeoutMs: null` with a rationale comment (identical runtime semantics: null stopwatch, no budget check): SmapiManagement InteractionModel.Update (2000ms backoff deploy loop), LwaClient.LwaDeviceAuth, LwaClient.LwaExchangeCode. LwaClient.LwaRefreshToken keeps its explicit 15_000 (JF-544); BaseHandler.StartSessionLookup keeps its explicit per-call timeoutMs (JF-477 2s fast-fail).

3. TWIN CONSOLIDATION. All SIX wrappers are now pure expression-bodied delegations to ExecuteWithRequestBudgetAsync, zero retry/budget logic of their own (census grep: no private wrapper wires a budget into ExecuteWithRetryAsync anymore): BaseHandler.RetryAsync (kept protected: ~65 handler call sites; uses the shared default budget), SearchService/CrossMediaFallback/AlbumPlayService/TvNextUpService.RetryAsync (pass their composition-injected `_requestTimeoutMs`; the ctor seam is preserved so tests can construct with explicit budgets), RadioTrackSource.RetryAsync (the only one keeping a parameter: the JF-576 `timeoutMs > 0 ? timeoutMs : _requestTimeoutMs` override). The composition-time injection into all five collaborators is unchanged and still compiles; ctor param docs updated to name the RetryHelper home.

4. TDD + VERIFICATION. Tests written first (RED: CS0117 missing members), then green: RetryHelperTests gains AlexaRequestTimeoutMs_ValueIs6000, ExecuteWithRetryAsync_TimeoutDefaultsToSharedBudget + ExecuteWithRequestBudgetAsync_TimeoutDefaultsToSharedBudget (reflection on the compiled parameter defaults, both overloads), DefaultBudget_StopsRetries_WithoutExplicitTimeout (behavioral: delay 7000 > 6000 budget stops after 1 attempt, no delay waited), ExecuteWithRequestBudgetAsync_ExplicitBudget_StopsRetries (the override shape); the old NoTimeoutSpecified_RetriesAsBefore became ExplicitNullTimeout_DisablesBudget_RetriesAllAttempts (passes timeoutMs: null) because its premise (omitted timeoutMs = unbounded) is exactly what JF-572 inverted. Full suite: 3994/3994 PASSED on BOTH net9.0 and net10.0 (was 3939 pre-batch-wave; +5 here, rest from the later batches). `dotnet build -c Release`: 0 warnings, 0 errors. No test referenced the private wrapper internals, so no twin-test adjustments were needed.
<!-- SECTION:NOTES:END -->
