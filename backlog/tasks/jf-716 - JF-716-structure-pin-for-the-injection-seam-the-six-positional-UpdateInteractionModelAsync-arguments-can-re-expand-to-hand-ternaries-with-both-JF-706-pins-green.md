---
id: JF-716
title: >-
  JF-716 - structure pin for the injection seam: the six positional
  UpdateInteractionModelAsync arguments can re-expand to hand ternaries with
  both JF-706 pins green
status: Done
assignee: []
created_date: '2026-10-02 16:10'
updated_date: '2026-10-03 11:42'
labels:
  - catalog
  - structure-pins
dependencies:
  - JF-706
references:
  - >-
    backlog/tasks/jf-706 -
    JF-706-collapse-the-three-SyncTypeLegAsync-call-sites-into-a-loop-over-a-type-tuple-list.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-706 merge
(commit 1a2f7651, finding 3 of 5). The JF-706 structure pins scan the SyncTypeLegAsync
and SyncCatalogForLocaleAsync call tokens and close the leg-loop side, but NEITHER
covers the injection seam: the injection gate (`minted.Count > 0`) and the six
positional id/version arguments into UpdateInteractionModelAsync
(LibrarySyncService.cs ~262-285) can be re-expanded into hand-written per-type ternaries
(the pre-JF-706 shape) or a second gate with both pins green, silently reopening the
JF-495 hand-ternary hazard JF-706 closed on the leg side.

THE WORK: a third structural pin closing the injection side. Candidate shape (from the
review): a NEGATIVE assertion that RunLegAsync's MoveNext body never loads the three
stored-id getters (user.ArtistCatalogId / user.AlbumCatalogId / user.SeriesCatalogId -
the new code reads ids only from the minted collection, so the hand-ternary shape, which
reads those getters, cannot appear without tripping the scan). Mind: IlCallScanner scans
call opcodes (0x28/0x6F); a field-load scan needs ldfld (0x7B) support, so decide
between extending the scanner or a targeted scan helper. The getter IS written
elsewhere (the if/else chain at ~558-569), so scope the assertion to RunLegAsync's body
only. Alternative accepted shape if the scanner extension is disproportionate: a
CatalogManager-side allowlist scan (per the review's alternative), or the JF-711
setter-threading design landing first and making the negative assertion moot - in that
order of preference, not in parallel.

GATE-MARKER TAIL (2026-10-03, orchestrator scaled review of commit 8fa4d207, 4 findings,
all applied; the scaled axes verified against source: the auto-property claim CONFIRMED
with Roslyn never bypassing auto-property accessors at IL level (inlining is JIT-only),
the ldfld refutation sound, no C# source able to name a backing field; the reviewer ran
the class fresh 3/3 both TFMs and re-derived the red paths): F1 (GetAccessors(true) so a
future internal-set refactor keeps setter coverage instead of silently dropping from the
pin, probed public-only behavior); F2 (the property list DERIVED from every User
*CatalogId property, so a fourth synced type's id joins the pin automatically - the exact
one-row edit JF-711 made compile-loud - instead of arriving unpinned); F3 (the failure
message names WHICH conjunct failed, count or shape, instead of "exactly one place" next
to an x1 list); F4 (DescribeCallSites appends a decoded source-level hint - local
function N, lambda, state machine of N - while the raw mangled name stays the pinned
fact). Class 3/3 both TFMs after the tail.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (solution build, both TFMs, 0 errors; the only warnings anywhere are the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1499, the other worker's untouched file)
- [x] #2 dotnet test passes (5014/5014 net9.0 and 5014/5014 net10.0, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` exit 0; baseline 5013 + 1 new pin. The full runs executed on the pre-gate build; the post-gate final state (message-wording and walk-restructure changes, the class still exactly 3 facts, suite count unchanged) verified by filtered class runs 3/3 on both TFMs)
- [x] #3 No new compiler warnings introduced (every post-change build grep-clean apart from the pre-existing xUnit1030 pair; the new pin, its helpers, and the Assert.True rewordings produced no analyzer hits)
- [x] #4 N/A (no session attributes touched; IL-scan test only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (test-only structure pin; ZERO production lines changed. Proof status, LIVE-run not read-level: three sabotage red proofs executed against real production edits, each failing the pin with the offender named. (1) the injection-seam hand ternary (Minted(Artist).CatalogId replaced with a direct user.ArtistCatalogId read) failed naming RunLegAsync's state-machine MoveNext as the second caller; (2) the pre-JF-711 CatalogType-keyed if/else write-back replacing storeCatalogId(catalogId) failed naming SyncCatalogForLocaleAsync's MoveNext; (3) the getter hoisted into a plain private helper (count stays exactly 1, only the shape check can fire) failed naming the helper. (1) re-proven after the simplify restructure on the final assertion code; the /code-review agent independently re-derived red paths A and B again on the final state)
- [x] #8 N/A (no Alexa speech)
- [x] #9 /simplify passed (4 parallel angles. APPLIED: the single-assembly-walk restructure (MethodCallTokens materialized once, six accessor tokens matched in memory via the CallSites overload pair, replacing six full IL walks); the Sum idiom aligning the assertion with AssertSingleCallSite; the class-summary trim (the shape enumeration lives in the method doc, the one owner); the shared DescribeCallSites failure formatter; the named failure message on the property lookup. SKIPPED with reasons: inlining AssertOnlyCallerIsTableLambda into the loop (mirrors the sibling AssertSingleCallSite naming pattern, the agent itself did not push it); nameof instead of string literals for the three property names (the file's convention is string literals for reflection lookups, and the runtime lookup now fails with the property named in the message))
- [x] #10 /code-review high passed (1 finding, APPLIED: the failure message now says what the check enforces, "a lambda compiled in SyncUserLibraryAsync (the wiring-table rows' shape)", not the stronger "a row lambda of the wiring table"; the residual gap it names, any ad-hoc b__ lambda under the same owner passing at count 1, is documented in the pin's BOUNDARY paragraph as adjacent to the accepted deliberate-evasion class. The same round also applied the named-message rewording of the sibling JF-706 lookup Assert.NotNull(worker), flagged in the simplify round. No unfixed out-of-scope findings remained, so no JF-728 filing)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 8fa4d207 + gate-marker tail 29cb0ca5 + the simplify-round fix 6493bda9, merged as dc24b2f7. The whole-assembly closure pin: every User *CatalogId accessor must have exactly one call instruction in the entire plugin, in a compiler-generated wiring-table lambda; the property list is DERIVED (a fourth synced type joins automatically) with a SUBSET guard keeping the rename tripwire loud; GetAccessors(true) and NonPublic GetProperties close the silent-loss classes at both levels. The planned ldfld/stfld extension REFUTED with evidence (auto-properties compile to accessor calls; no C# source can name a backing field). THREE live sabotage red proofs against real production edits. Worker gates green (simplify 5 applied; code-review high 1 applied); the scaled orchestrator review verified the auto-property claim at source and its 4 findings applied; the closure-gate /simplify round's 4 angles then empirically exposed a defect in the orchestrator's own decode fix (the d__ branch read the method name where Roslyn puts the marker on the type) - fixed with the (Type, Method) pair and the correct slice. Suites: worker full 5014/5014 both TFMs (discipline held, prediction matched), class 3/3 after each round, merged-tree 5014/5014 both TFMs exit 0 on both split legs. Test-only: no production surface, no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
