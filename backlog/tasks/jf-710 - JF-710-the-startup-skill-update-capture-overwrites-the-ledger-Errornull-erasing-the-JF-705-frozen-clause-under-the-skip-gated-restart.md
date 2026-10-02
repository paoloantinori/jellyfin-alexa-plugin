---
id: JF-710
title: >-
  JF-710 - the startup skill-update capture overwrites the ledger Error=null,
  erasing the JF-705 frozen clause under the skip-gated restart
status: Done
assignee: []
created_date: '2026-10-02 13:55'
updated_date: '2026-10-02 21:29'
labels:
  - catalog
  - observability
dependencies:
  - JF-705
references:
  - >-
    backlog/tasks/jf-705 -
    JF-705-per-locale-model-status-ledger-records-SUCCEEDED-over-a-partially-frozen-leg-surface-the-frozen-types-in-the-admin-UI.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-705 merge
(commit 7a7982ee, finding 1 of 2). `SkillStartup.CaptureLocaleModelStatusesAsync`
(SkillStartup.cs ~375-395) runs after the startup skill UPDATE and unconditionally
overwrites each locale's ledger entry with the fresh build's outcome, including
`Error = null` on success. That is honest for the MODEL-BUILD surface, but the JF-705
frozen clause describes CATALOG state from the last sync, which a model rebuild does
not reset, so the overwrite erases "Artist catalog FROZEN (last-good pinned)" next to
a green SUCCEEDED.

The failure needs a compound path, which is why it is filed rather than tailed: (1) a
catalog sync froze a type and wrote the clause; (2) the plugin restarts with a version
change, so `cloudVersion != GetVersion()` triggers UpdateSkillAsync + the capture,
which wipes the clause; (3) the startup catalog re-sync is skipped via the
CatalogSyncTask.cs:68/96 gates (SmapiDeviceToken null, SkillId null, or the Jellyfin
user deleted) so no sync re-writes it. The admin page then shows green SUCCEEDED with
no clause while the artist catalog stays pinned to last-good (the exact stale-green
triage trap JF-705 was filed to close. The 12h self-heal only covers the
credentials-intact case (Success=false keeps LastCatalogSync stale so the re-sync gate
cannot skip). Note the same overwrite also erases JF-495 canary errors (pre-existing
pattern this change extends; fix both in one shape).

THE WORK: when the captured entry would be Status=SUCCEEDED with Error=null and the
EXISTING entry's Error carries a FROZEN clause, preserve the existing clause (the
condition it describes; the last sync froze types and no sync has run since) is
still true at capture time; the next sync either re-freezes and rewrites it or heals
and clears it). Extract the clause composer so the marker check is not a bare string
literal duplicated at two sites. Add a startup-path pin for the preserve (and the
clean case not inventing a clause). Mind: this is a second ledger writer; read
JF-709's Status-for-no-build open decision before choosing where the preserve logic
lives, so the two do not fight over the same entry.

COORDINATION UPDATE (2026-10-02, JF-709 rework round): the ledger now has THREE
writers, namely the JF-705 PUT-path writer (RecordModelUpdateInLedger), the JF-709
no-PUT writer (RecordNoPutFrozenLegInLedger), and this startup capture. JF-709's
Status-for-no-build decision is CLOSED: settled statuses (SUCCEEDED / FAILED /
TIMEOUT, OrdinalIgnoreCase) are preserved verbatim by the no-PUT writer, transients
and unknowns clamp to "Skipped" (PreservedOrSkippedStatus), Source is always the
writing subsystem's own label. This task's preserve spec must therefore say WHICH
Error segments survive a capture overwrite: the frozen clause
(LibrarySyncService.FrozenLedgerClause output) and any FOREIGN diagnostic (a JF-495
canary message or failed-PUT reason, i.e. an Error NOT carrying the JF-709 no-PUT
tail) MUST survive; the run-scoped "; no PUT this run" tail itself must NOT survive
(it describes the last sync run's shape, which the capture's rebuild supersedes in
observability terms), so drop it on overwrite rather than preserving stale
run-scoped text. The no-PUT tail constant (LibrarySyncService.NoPutLedgerTail) is
the marker both this capture and the no-PUT writer's own-shape replace key on; it is
internal, reuse it, do not re-type the literal. ONE SHAPE HAZARD to design around:
if the capture preserves a frozen clause WITHOUT the tail, the next all-frozen run's
own-shape check (tail-based) will classify that clause as foreign and emit a
self-referential "; previous: <same frozen clause>" trail for one run; make the
own-shape predicate there recognize the frozen-clause-led shape too (clause prefix
OR tail marker), not the tail alone.

GATE-MARKER TAIL (2026-10-02, orchestrator review of commit a09a0448, 5 low findings,
all five scrutiny axes verified clean with the pins empirically re-run 21/21): F4
APPLIED (the four banned hyphen-break constructions in the Final Summary prose fixed,
plus three pre-existing ones in the description); F5 APPLIED (the two unpinned preserve
shapes gained their pin: the plain clause+tail row reduces to clause-only and stays
idempotent under a second capture); F1 APPLIED as a comment (the KNOWN RACE note at the
capture's read: the preserve is an unguarded read-modify-write while the sync writers
guard against the capture, one-directional by accepted trade); F2 APPLIED (the
LOAD-BEARING INVARIANT doc on IsOwnShapeLedgerError naming the three-literals
assumption and pointing at JF-721); F3 FILED as JF-721 (the structured-caveat design
replacing the marker-parser family; sequenced after JF-719's gate decision).
Independent suite 4970/4970 both TFMs on the worker commit; affected classes 22/22
after the tail.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (JF-710 worker 2026-10-02: `dotnet build Jellyfin.Plugin.AlexaSkill.sln` succeeded, both TFMs, on the final state)
- [x] #2 dotnet test passes (JF-710 worker 2026-10-02: full suite 4970/4970 BOTH TFMs on the final state, `-m:1`, exit 0; baseline 4962 + 8 new pins)
- [x] #3 No new compiler warnings introduced (only the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched; ledger rows are the existing LocaleModelStatus DTO)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no intent/handler logic; the change is startup-ledger observability, pinned through the internal CaptureLocaleModelStatusesAsync seam with a faked GetSkillStatusAsync, the JF-366 pattern)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings)
- [x] #9 /simplify passed (4 parallel cleanup agents: 7 findings applied, 2 skipped with reasons recorded in the commit message)
- [x] #10 /code-review high passed (5 findings: F1 applied with a new idempotence pin, F3/F4/F5 applied as doc/prose fixes, F2 already tracked as JF-719; 0 rejected)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit a09a0448 + gate-marker tail 27edcc89, merged as d3b945ac. The startup capture preserves catalog-sync ledger diagnostics on a clean SUCCEEDED capture: PreserveLedgerErrorAcrossCapture is content-keyed (idempotent across captures, pinned twice), byte-identical Remove+Replace over the two sync writers' shared literals, keeping the frozen clause and foreign diagnostics while dropping the run-scoped tail and previous-framing; IsOwnShapeLedgerError closes the clause-without-tail hazard; FAILED captures still replace wholesale. 8 worker pins driven through the real capture seam (reflection-verified Alexa.NET.Management shapes) plus the gate-marker tail's two-shape pin. Worker gates green (simplify 7 applied; code-review high 5 applied incl. the F1 content-keyed idempotence); JF-719 filed (the IN_PROGRESS clean-capture residual, gate-vs-settle-wait decision left to the coordinator; my read: widen the gate to any clean capture when taken, since the settle-wait adds startup latency to protect a sub-case the content-keyed preserve already handles safely). Gate-marker verified all five scrutiny axes with empirical pin re-runs; its 5 low findings all landed (prose fixes, the two-shape pin, the race and invariant docs, JF-721 filed for the structured-caveat design). Suites: worker and orchestrator independent 4970/4970 both TFMs, merged-tree 4971/4971 both TFMs as concurrent split-TFM jobs. Production surface changed (SkillStartup, LibrarySyncService): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
