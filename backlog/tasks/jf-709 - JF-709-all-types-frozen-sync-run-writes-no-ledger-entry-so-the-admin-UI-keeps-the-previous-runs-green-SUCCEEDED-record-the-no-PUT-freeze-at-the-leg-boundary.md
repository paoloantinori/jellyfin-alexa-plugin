---
id: JF-709
title: >-
  JF-709 - all-types-frozen sync run writes no ledger entry, so the admin UI
  keeps the previous run's green SUCCEEDED; record the no-PUT freeze at the leg
  boundary
status: To Do
assignee: []
created_date: '2026-10-02 12:00'
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
Filed 2026-10-02 same-turn from the JF-705 /simplify gate (altitude agent, finding 1 of
2; number reserve: max existing was JF-707 plus this session's JF-705). JF-705 threads a
leg's frozen types into the ledger entry on the model-PUT path
(`RecordModelUpdateInLedger(locale, modelUpdate, frozenTypes)`), which fixes the
PARTIAL freeze: PUT succeeded, one type froze, the entry's Error field now carries
"Artist catalog FROZEN (last-good pinned)" next to the green SUCCEEDED check. The
residual is the TOTAL freeze shape: the ledger write sits inside the PUT gate
(LibrarySyncService.cs, the `if (artistVersion != null || albumVersion != null ||
seriesVersion != null)` block in RunLegAsync), and when ALL versions are null the gate
skips both the PUT and the ledger write. The JF-705 task text framed that shape as
"already stays OUT of the ledger", which is true only in the no-previous-entry sense:
on a server that ever had a healthy run, the locale KEEPS the previous run's clean
SUCCEEDED entry while every catalog type is now pinned to last-good. The failure an
admin sees is the same "all locales green, artist recognition stale" triage trap
JF-705 was filed for, in the worse shape. Reachable forms: all three types frozen, or
one type frozen plus zero-item siblings (the zero-items shape also yields a null
version per the JF-495 rule documented at the call site), and the JF-513.3
all-types-hash-skipped leg (every payload byte-identical to an earlier locale's this
run, so all versions are null with nothing frozen at all; today masked because ar-SA
is excluded and hi-IN is the only remaining no-generator locale, but reachable the
moment another no-generator locale is added or the exclusion is lifted). Note the
hash-skip form is NOT a freeze: decide whether it deserves a ledger clause at all or
is fine leaving the previous entry untouched, since its model genuinely still
references current catalog versions.

THE WORK: record the freeze at the LEG boundary, not the PUT boundary. The locale
attempt loop already consumes `legFrozenTypes` after `RunLegAsync` returns (the
`legFrozenTypes is { Count: > 0 }` branch that merges into `result.FrozenTypes`), and
that is where a no-PUT freeze should append the frozen clause to the locale's EXISTING
ledger entry. Open decision to make when picking this up: what Status an entry carries
when this run performed no model build at all (candidates: keep the previous entry's
Status and append the clause to its Error, matching the JF-705 shape of
"caveat-without-failure"; or an observation status in the UNVERIFIED family). Keep the
run-level honesty unchanged (FrozenTypes gates Success=false either way). Watch the
leg-retry interaction: types frozen on a retried-away attempt refreeze on the winning
attempt, so only the returned attempt's list is truthful (same caveat the merge site
documents). This change adds a FOURTH frozen-types string format next to the three
already in LibrarySyncService (the run-level completion clause, the run-level
LogWarning, and the JF-705 ledger clause): when landing it, decide whether to
consolidate them behind one formatter (the JF-705 code-review finding 3, skipped
there as maintainability-only with intentionally different per-surface wordings) or
keep the per-surface wordings and say so in the code.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Pin: all-types-frozen run with a pre-existing green entry surfaces the freeze in that locale's ledger entry (and the clean run leaves no clause)
- [ ] #5 /simplify passed (no blocking cleanups remaining)
- [ ] #6 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
