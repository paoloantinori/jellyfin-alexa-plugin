---
id: JF-846
title: >-
  Maintenance-window transparency: during catalog syncs and model deploys,
  spoken responses say the skill is updating and ask for patience (the
  SkillWarmingUp pattern extended)
status: To Do
assignee: []
created_date: '2026-10-09 11:54'
labels:
  - ux
  - resilience
  - pipeline
milestone: m-18
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-09 by the orchestrator from the maintainer's request during a live catalog-sync window ("when the skill is self-maintaining, invocations should say so and ask for patience instead of leaving users thinking it's broken").

DESIGN (agreed with the maintainer):
- A static maintenance scope (e.g. CatalogSyncScope/MaintenanceMode) opened by the catalog-sync task at start and closed at end, and by the custom-model rebuild endpoint during its PUTs. No persisted state.
- A RequestPipeline interceptor (or the existing warming-gate plumbing, the SkillWarmingUp precedent JF-419) checks the scope: while open, spoken responses gain a SHORT prefix ("Sto aggiornando la skill, un attimo di pazienza..." per locale) and library-heavy requests that would collide with the in-flight model rebuilds can take the warming-style Tell.
- Honest scope note: during a sync, voice requests mostly WORK (Amazon serves the previous model until the new build completes; Jellyfin queries don't contend with the sync). The prefix informs without blocking. The container-restart window during DLL deploys (~45s, server down, device shows the platform error) is NOT coverable server-side and is excluded.

DELIVERABLES: the scope type + open/close at the sync task and the rebuild endpoint; the interceptor branch; the locale key(s) in all 17 locales + the AllExpectedKeys ledger; unit pins (scope open prefixes, scope closed does not, the warming-style fallback shape if taken); the warming-gate coverage test updated if a new gated path is added. Consider ALSO logging the window start/end at Information so triage can correlate user complaints with maintenance windows (the debug-logging policy).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 SkillMaintenanceScope (Alexa/Util/SkillMaintenanceScope.cs): int-refcounted static, thread-safe (Interlocked increment/decrement + Volatile.Read), IDisposable Open token with double-dispose guard; window open/close logged at Information with the operation name (the triage correlation)
- [x] #2 Scope opened at the catalog-sync entry (LibrarySyncService.SyncUserLibraryAsync, after the no-work skip guards, using-scope so every exit closes it) and by the custom-model rebuild endpoint (ConfigurationController.RebuildModels, around the RedeployAsync PUT loop)
- [x] #3 SkillMaintenanceInterceptor (Alexa/Pipeline) prepends the SHORT localized SkillUpdatingPrefix to responses that already carry an OutputSpeech (plain text prepended; SSML inserted INSIDE the speak tags, XML-escaped); directive-only responses (VideoApp launches, AudioPlayer plays), the docs-mandated silent stop shape, and event requests (JF-507) are never given speech; reprompt untouched; registered LAST so it executes FIRST (before ResponseBodyLoggingInterceptor's snapshot, so the logged body shows what the user heard)
- [x] #4 SkillUpdatingPrefix present in all 17 locale files + the ResponseStringsTests AllExpectedKeys ledger (JF-821 convention)
- [x] #5 Pins (SkillMaintenanceInterceptorTests): open prefixes plain/SSML/Ask (reprompt untouched)/request-locale; closed leaves speech byte-identical; directive-only, silent stop, and event shapes untouched while open; refcount nesting (prefix stops only after BOTH openers close); double-dispose; Information window logging; PrefixIntoSsml wrap theory
- [x] #6 Full suite green both TFMs; validate_locales passes; no new warnings (Release build 0/0)
- [x] #7 The warming-gate machinery (JF-419) untouched: response-side only, no new gated handler (WarmingGateCoverageTests roster unchanged)
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-10-09 DONE (implemented on the agent worktree branch; orchestrator merges).

- Scope shape: `internal static class SkillMaintenanceScope` (the IndexWarmingGate
  idiom: openers and reader never share a DI lifetime). `Open(operationName, logger?)`
  returns a private `IDisposable` whose construction increments and whose Dispose
  decrements once (guarded). `IsOpen` is a Volatile.Read over the int.
- Interceptor registration order decision: registered LAST among
  IResponseInterceptor registrations, so the pipeline's REVERSE execution order
  runs it FIRST, ahead of ResponseBodyLoggingInterceptor. The documented gotcha
  (the logging snapshot excludes later mutations) is used FOR us here: the logged
  response body carries the maintenance prefix the user actually heard, which is
  the triage correlation this task asks for. Running it last-in-registration also
  keeps it ahead of SessionAttributesInterceptor/DynamicEntities mutations, which
  never touch speech.
- The honest-window choice: the sync-side scope opens in
  SyncUserLibraryAsync AFTER its no-work skip guards (no SMAPI token/skill,
  no vendor), so a window exists only while real SMAPI maintenance is in flight;
  the scheduled task's 12h-skip users (no work) do not open one. Per-user syncs
  nest naturally through the refcount.
- Red-run catch (fixed before green): the first scope implementation put
  `Interlocked.Increment(ref _refCount)` INSIDE a `_logger?.LogInformation(...)`
  argument list; with a null logger the null-conditional call never evaluates its
  arguments, so the refcount never moved and IsOpen stayed false. The scope pins
  caught it in isolation; the mutation is now hoisted above the log call with a
  comment pinning the constraint.
- Deliberate boundary (per the dispatch's hard boundary): the invocation-name
  save path (ConfigurationController.UpdateUserSkill, which shares
  IInteractionModelRedeployer.RedeployAsync with the rebuild endpoint) is NOT
  wired as an opener; the agreed design names exactly two openers (catalog sync
  + custom-model rebuild endpoint). Wiring the third site is a one-line change
  at the same call shape if the maintainer wants it.
- The warming-style fallback Tell (library-heavy requests taking the SkillWarmingUp
  answer during a window) was NOT taken: the honest-scope note in the design says
  requests mostly work during a sync, so the prefix informs without blocking; no
  new gated path exists, so WarmingGateCoverageTests is untouched.
- /simplify (4 agents: reuse, simplification, efficiency, altitude). APPLIED 6:
  ILogger required on Open (dropped the null-conditional and its trap comment;
  both production openers always passed a logger), the tests' 7x open/try/process
  /finally scaffold collapsed into one ProcessWhileOpenAsync helper (using-scoped,
  cannot leak), TestHelpers.CreateTestContext replaces the hand-built Context,
  PrefixIntoSsml wrapped branch rewritten to string.Insert, the scope's static
  rationale rewritten to the IndexWarmingGate ambient-signal precedent (the first
  draft's "openers and reader never share a DI lifetime" claim was factually
  wrong: all three resolve from the same collection), and the sync-side comment
  now names the inter-user delay gap (the window honestly rests between users).
  SKIPPED with reasons: PrefixIntoSsml hoist to SpeechBuilder (single caller;
  this repo hoists at the THIRD caller per extraction-on-convergence), dropping
  the defensive SSML wrap arm (documented + theory-pinned, 3 lines), PrefixKey
  const to literal (couples test+prod key name), and the altitude finding to move
  the rebuild opener into InteractionModelRedeployer.RedeployAsync (would cover
  the invocation-name path too, but the dispatch's hard boundary freezes the
  opener surface at exactly the two designed sites; recorded above). Efficiency
  agent: clean, no findings (guard order confirmed cheapest-first).
- /code-review high (5 findings: 3 applied, 2 rejected with reasons). APPLIED:
  (F1) the process-global scope race: SkillMaintenanceInterceptorTests and
  LibrarySyncServiceTests (the ONLY uncollected class whose tests really call
  SyncUserLibraryAsync; the review's Structure/Emptiness enumeration was
  over-broad, they mention the method only in comments/IL strings, verified by
  call-site grep) now carry [Collection("Plugin")], and PluginCollection.cs's
  statics inventory names SkillMaintenanceScope. Without this, a parallel-phase
  sync test's open window could prefix the scope-asserting pins intermittently.
  (F3) PrefixIntoSsml keyed its insert arm on leading AND trailing speak tags,
  so a "<speak>...</speak>\n" tail fell into the wrap arm and NESTED a second
  speak root (invalid SSML); the insert now keys on the leading tag only, with
  a theory row pinning the trailing-newline shape. (F4) Scope.Dispose's
  double-dispose guard was a plain bool read-then-write (two concurrent
  disposes could double-decrement); now an Interlocked.Exchange int guard.
  REJECTED with reasons: (F2) per-skill window keying (user B hearing the
  notice during user A's sync): the agreed design and the dispatch both fix
  the scope as ONE int-refcounted static; per-skill keying is a redesign past
  the frozen boundary and the interceptor has no resolved user (auth is
  handler-side); the multi-user-simultaneous-sync tradeoff is recorded here.
  (F5) prefixing the progressive-response announcements too: the interceptor
  is response-side only by the dispatch's boundary, the progressive path is a
  separate direct-HTTP mechanism outside the response chain, and the final
  response carries the notice.
<!-- SECTION:NOTES:END -->

## Final Summary
<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-09 on the agent worktree branch (feat(jf846); orchestrator merges): maintenance-window transparency ships as three pieces. (1) SkillMaintenanceScope (Alexa/Util): internal static, int-refcounted (Interlocked + Volatile), IDisposable Open token with an Interlocked double-dispose guard; open/close log at Information with the operation name. Opened by LibrarySyncService.SyncUserLibraryAsync after its no-work skip guards (using-scoped, every exit closes) and by ConfigurationController.RebuildModels around the RedeployAsync PUT loop; the invocation-name path stays unwired per the frozen two-opener boundary. (2) SkillMaintenanceInterceptor (Alexa/Pipeline), registered LAST so it executes FIRST in the reverse response chain, before ResponseBodyLoggingInterceptor's snapshot (the logged body shows the prefixed speech the user heard): while a window is open it prepends the SHORT localized SkillUpdatingPrefix to responses that already carry OutputSpeech (plain prepended, SSML inserted inside the speak tags, XML-escaped, keyed on the LEADING speak tag only); directive-only responses, the docs-mandated silent stop shape, and event requests never gain speech; reprompt untouched. (3) SkillUpdatingPrefix in all 17 locales (en "I'm updating the skill, one moment please...", it "Sto aggiornando la skill, un attimo di pazienza...", de/es/fr/pt/nl/ja/hi/ar in kind) + the AllExpectedKeys ledger row. RED-GREEN: the five prefix pins ran red against the guard-only interceptor skeleton on BOTH TFMs (5 failed/7 passed each), green after the mutation; the red round also caught a real bug (the refcount increment lived inside a null-conditional log call and never ran with a null logger; hoisted and later made moot by the required-logger simplify fix). Pins: 16 total (open prefixes plain/SSML/Ask/locale, closed byte-identical, directive-only, silent stop, event request, refcount nesting, double dispose, Information logging, 4-row PrefixIntoSsml theory). Gates: /simplify 4 agents (6 applied, 4 skipped with recorded reasons), /code-review high (5 findings: F1 scope/test parallelism race APPLIED via [Collection("Plugin")] on the two classes + the PluginCollection inventory, F3 nested-speak corruption APPLIED, F4 dispose hardening APPLIED, F2 per-skill keying and F5 progressive-prefix REJECTED with recorded reasons). Verifiers: validate_locales PASS; Release build 0 warnings 0 errors; full suite 5601/5601 BOTH TFMs on the final tree (baseline 5585 + 16). Not deploy-gated: rides the next batch deploy; the live device check is to invoke any spoken request during a manual "Rebuild models" and hear the prefix.
<!-- SECTION:FINAL_SUMMARY:END -->
