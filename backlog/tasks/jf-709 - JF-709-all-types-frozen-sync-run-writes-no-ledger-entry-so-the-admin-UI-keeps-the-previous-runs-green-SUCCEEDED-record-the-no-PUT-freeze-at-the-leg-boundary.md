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
priority: high
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

AUDIT UPDATE (2026-10-02, from the JF-706 code-review round): this task's premise for
the hash-skip form ("its model genuinely still references current catalog versions")
is wrong for any locale whose EVERY run is all-skipped, and that shape is live today
at scale, not a future-reachable edge. The uploaded payload depends on the locale
only through the synonym generator, which is keyed by language prefix
(Util.LocalePrefix.Of), and the seed enrichment is a locale-INDEPENDENT union across
the committed models (CatalogSeedEnrichment), so the byte-identical equivalence
classes are the prefix families: {es-ES, es-MX, es-US}, {fr-FR, fr-CA}, and the
no-generator cluster {en-AU, en-CA, en-GB, en-IN, en-US, hi-IN} (generators cover
it/de/es/fr/pt/ja/nl prefixes only; ar-SA is excluded by JF-543). Under the default
"*" config only the FIRST member of each class to run uploads and gets its model
PUT; every later member returns null versions for all three types on EVERY run, the
injection gate (identical before and after JF-706) skips the PUT, and since the
embedded models carry zero valueCatalog blocks those locales' interaction models
NEVER receive catalog references: catalog ER never activates there. That is 8 of
the 16 synced locales today (2 es + 1 fr + 5 en/hi). The observability gap this task
files (no ledger entry) and this product gap (no wiring, ever) share the same
trigger and the same fix boundary (the leg boundary, where a no-PUT outcome should
be handled explicitly); whoever picks this up should treat the starved-locale shape
as a first-class case to decide, not only the freeze shapes; options include
keying the hash-skip per locale rather than per catalog (a skipped locale still
needs its OWN model wired to the shared catalog version), or recording and
surfacing "identical to <earlier locale>, not re-wired" so the admin sees why the
locale is unwired. Note a narrow config (e.g. "es-MX" alone with it-IT) wires the
locale fine; only the shared-default multi-locale run starves it. The stale outer
JF-513.3 comment that claimed the skip "returns the last uploaded version ... treats
as current" was corrected in the JF-706 change; this file is now the only record of
that historical wrongness.

AUDIT UPDATE 2 (2026-10-02, JF-706 gate-marker round, code-CONFIRMED): the starvation
analysis is verified end to end - CatalogPayload.cs:45 and CatalogSeedEnrichment.cs:211
are the only locale consumers, PhoneticSynonymGenerator.cs:42-54 dispatches purely on
LocalePrefix (empty for everything else), the seed union is locale-independent, and
UpdateInteractionModelAsync is the sole wiring path. The byte-identical classes are
exactly {es x3}, {fr x2}, {en-AU/CA/GB/IN/US + hi-IN x6}; under the default "*" config
8 of 16 synced locales NEVER get their model PUT (the first member of each class to run
uploads, the rest hash-skip forever; CatalogWiringGraft cannot help, it only preserves
existing wiring). PRIORITY RAISED to high. A JF-706 gate-marker finding adds the
hash-skip ordering hazard to this family - see JF-703's audit addendum.
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
