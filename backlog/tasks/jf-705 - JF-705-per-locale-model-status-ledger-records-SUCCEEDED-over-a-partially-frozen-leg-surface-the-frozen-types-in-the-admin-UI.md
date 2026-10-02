---
id: JF-705
title: >-
  JF-705 - per-locale model-status ledger records SUCCEEDED over a partially
  frozen leg; surface the frozen types in the admin UI
status: To Do
assignee: []
created_date: '2026-10-02 10:12'
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
Closed by the JF-705 worker (worktree agent-a2059cf67e47e4eaa, branch off ba8e72bf). CONSUMER
READ (the decision's evidence): config.html's loadCustomModelStatus renders each locale entry
with a green check iff status === "SUCCEEDED", shows the raw status text only when not
SUCCEEDED, and renders the error field UNCONDITIONALLY when non-empty (substring(0,80)
visible, full text in the title tooltip); DiagnosticsController.GetPanel matches Status
strings only (failedModels = FAILED/TIMEOUT; ModelsDeployed = Any "Succeeded") so a distinct
status value would flip ModelsDeployed false for a locale whose model build actually
succeeded; ConfigurationController.GetCustomModelStatus passes status/error through
untouched; SkillStartup.CaptureLocaleModelStatusesAsync is a fellow writer, not a parser.
DECISION: keep Status as the PUT's own outcome and surface the frozen types in the Error
field (the ledger's existing caveat channel, same as JF-495 canary mismatches); no
config.html change needed, which also avoids the embedded-resource clean-build dance. WHAT
LANDED: RunLegAsync passes its frozenTypes list into RecordModelUpdateInLedger, which
composes "Artist + Album catalogs FROZEN (last-good pinned)" (plural and " + " join over the
enum names, 58 chars worst case (gate-marker correction; the commit message retains the off-by-one), inside the 80-char visible window) LEADING any canary
message so the persistent actionable condition survives truncation (code-review F1);
LocaleModelStatus.Error's doc now names the caveat role; TWO pins extend
LibrarySyncServiceLegIsolationTests: partial freeze with successful PUT surfaces the frozen
type then a clean re-run leaves no clause, and a two-type freeze plus a post-PUT canary
mismatch (new ServeCanaryMismatchAfterPut fake toggle) exercises the plural branch, the
combined-message branch, and the clause-leads order. The all-types-frozen shape (no PUT, no
ledger write, previous run's green entry stays) is deliberately OUT (the task text's own
scope) and is filed as JF-709 with the deeper leg-boundary fix and the JF-513.3 hash-skip
shape the reviewer added. SKIPPED with reasons: the shared frozen-types formatter (three
surfaces with intentionally different wordings; consolidation question parked inside JF-709,
the change that adds the fourth site), the pluralization-ternary simplification ("catalog
type(s)" reads worse in admin-facing text than a one-expression ternary), and three
low-value test-assert trims (NotNull converts a would-be ArgumentNullException into a clean
failure; the clean-leg Status assert distinguishes a fresh clean write from a stale entry).
Gates: Skill simplify (4 parallel angles) + Skill code-review high, both as literal Skill
calls in the transcript; suites 4927/4927 both TFMs on the final state. No deploy; do not
push.
<!-- SECTION:FINAL_SUMMARY:END -->
