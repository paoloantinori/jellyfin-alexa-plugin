---
id: JF-710
title: >-
  JF-710 - the startup skill-update capture overwrites the ledger Error=null,
  erasing the JF-705 frozen clause under the skip-gated restart
status: To Do
assignee: []
created_date: '2026-10-02 13:55'
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
not reset - so the overwrite erases "Artist catalog FROZEN (last-good pinned)" next to
a green SUCCEEDED.

The failure needs a compound path, which is why it is filed rather than tailed: (1) a
catalog sync froze a type and wrote the clause; (2) the plugin restarts with a version
change, so `cloudVersion != GetVersion()` triggers UpdateSkillAsync + the capture,
which wipes the clause; (3) the startup catalog re-sync is skipped via the
CatalogSyncTask.cs:68/96 gates (SmapiDeviceToken null, SkillId null, or the Jellyfin
user deleted) so no sync re-writes it. The admin page then shows green SUCCEEDED with
no clause while the artist catalog stays pinned to last-good - the exact stale-green
triage trap JF-705 was filed to close. The 12h self-heal only covers the
credentials-intact case (Success=false keeps LastCatalogSync stale so the re-sync gate
cannot skip). Note the same overwrite also erases JF-495 canary errors (pre-existing
pattern this change extends; fix both in one shape).

THE WORK: when the captured entry would be Status=SUCCEEDED with Error=null and the
EXISTING entry's Error carries a FROZEN clause, preserve the existing clause (the
condition it describes - the last sync froze types and no sync has run since - is
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
