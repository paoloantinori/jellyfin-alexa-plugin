---
id: JF-705
title: >-
  JF-705 - per-locale model-status ledger records SUCCEEDED over a partially
  frozen leg; surface the frozen types in the admin UI
status: Done
assignee: []
created_date: '2026-10-02 10:12'
updated_date: '2026-10-02 13:53'
labels:
  - catalog
  - observability
dependencies:
  - JF-695
references:
  - >-
    backlog/tasks/jf-695 -
    JF-695-JF-689-code-review-residuals-per-type-sync-leg-isolation-and-the-blank-name-contract-boundary.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-695 merge
(commit 1a48a53d, finding 1 of 6). The JF-695 isolation is honest at the RUN level
(SyncResult.FrozenTypes gates Success to false; run-level LogError names the frozen
types) but the PER-LOCALE model-status ledger still records a clean SUCCEEDED for a leg
whose model PUT succeeded while one of its catalog types froze: `RunLegAsync` calls
`RecordModelUpdateInLedger(locale, modelUpdate)` (LibrarySyncService.cs:252) whenever at
least one type minted a version, and the entry carries no frozen-type awareness. Failure
scenario: artist freezes in every locale while album/series mint; every leg's model PUT
succeeds and stamps Status=SUCCEEDED (the JF-695 isolation pin itself asserts
ledger.Status == "SUCCEEDED"), so an admin diagnosing "why is artist recognition stale"
sees all locales SUCCEEDED in the config UI and finds the freeze only by grepping server
logs. Note the all-types-frozen shape already stays OUT of the ledger (no version minted
means no PUT and no entry), so the gap is exactly the partial-freeze shape.

THE WORK: thread the leg's frozen types into `RecordModelUpdateInLedger` and surface them
in whatever field the config UI (config.html) renders for each locale entry. Before
choosing the shape, READ the ledger consumer: find where the per-locale status strings
are parsed/rendered (Configuration/ and config.html) and decide between appending a named
clause to an existing message/error field versus a distinct status value the UI already
tolerates. Add a pin: partial freeze with successful PUT must surface the frozen type in
the ledger entry. Keep the run-level honesty (Success gate) unchanged.

GATE-MARKER TAIL (2026-10-02, orchestrator review of commit 7a7982ee, 2 findings): all
four scrutiny axes verified clean at the consumer level (no consumer keys behavior off
Error-empty for SUCCEEDED rows; the clause leads and fits the 80-char window in every
arity; the pins are hermetic per-test with the locale pin restored; the all-frozen shape
writes no entry, JF-709 territory untouched). F1 FILED as JF-710 (the startup
skill-update capture overwrites Error=null and can erase the frozen clause under the
compound skip-gated restart; preserve-semantics on a second writer, needs its own
startup-path pin); F2 applied (the worst-case clause is 58 chars, not 59; the commit
message retains the off-by-one).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (full solution, both TFMs, 0 errors; the only warnings are the pre-existing xUnit1030 pair on the untouched VideoAudioControllerTests ConfigureAwait line, shifted to :1337 by the JF-700 merge)
- [x] #2 dotnet test passes (final state: 4927/4927 net9.0 and 4927/4927 net10.0; baseline 4925 + 2 new pins; the dual-TFM background run exited 0 and net9.0 was re-run standalone for the count line)
- [x] #3 No new compiler warnings introduced (the first build attempt surfaced CA1859 on the IReadOnlyList parameter and was fixed in-change by typing it List<CatalogType> per the analyzer's suggestion; only the pre-existing xUnit1030 pair remains)
- [x] #4 N/A (no session attributes touched; the change is sync-service to config-ledger only)
- [x] #5 N/A (no HttpClient construction or BaseAddress change)
- [x] #6 N/A (no interaction model, template, or fixture change)
- [x] #7 N/A (no new intent or handler; the new behavior is pinned by the two unit pins in LibrarySyncServiceLegIsolationTests, which drive the full SyncUserLibraryAsync leg against the fake SMAPI backend including the model PUT and canary)
- [x] #8 N/A (no Alexa speech; the frozen clause is admin-UI ledger text carried by the Error field the config page already renders)
- [x] #9 /simplify passed (4 angles; 2 applied: call-site comment trimmed to one line, LocaleModelStatus.Error doc updated to name the caveat role; 3 adjudicated skips with reasons: the pluralization ternary, the NotNull/Error assert pair, the clean-leg Status assert; 1 out-of-scope finding filed as JF-709)
- [x] #10 /code-review high passed (2 findings applied: the frozen clause now LEADS the combined Error so it survives config.html's 80-char truncation when a canary error co-occurs, and a second pin covers the plural and canary-composition branches plus the order; 1 skipped with reason: the shared frozen-types formatter is maintainability-only across intentionally different per-surface wordings, with the consolidation question recorded inside JF-709 which will add the fourth site; the reviewer's JF-709 scope note (the JF-513.3 hash-skip shape) applied to the JF-709 task file)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 7a7982ee + gate-marker tail e0b85d0a, merged as 46a84f66. The per-locale ledger surfaces frozen catalog types via the Error field with Status unchanged (the consumer-read decision: config.html and DiagnosticsController key on Status strings and never parse Error; a distinct status value would have flipped ModelsDeployed for locales whose build succeeded). The frozen clause leads any canary message to survive the 80-char truncation; two new pins cover the partial-freeze surface and the two-type + canary co-occurrence with the ServeCanaryMismatchAfterPut fake toggle. Worker gates green (simplify 2 applied, code-review high 2 applied, JF-709 filed for the all-frozen no-entry shape), worker suites 4927/4927 both TFMs, orchestrator independent suite exit 0 on the identical tree (worktree base = current main; only markdown differed), gate-marker review verified all four scrutiny axes clean with 2 findings landed same-turn (JF-710 filed for the startup-capture Error-null overwrite residual; the clause worst-case corrected to 58 chars). Production surface changed (LibrarySyncService): deployed in the post-merge deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
