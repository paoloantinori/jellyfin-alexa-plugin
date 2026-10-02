---
id: JF-721
title: >-
  JF-721 - the ledger Error field is a three-writer wire protocol; replace the
  marker-parser family with a persisted caveat field
status: To Do
assignee: []
created_date: '2026-10-02 21:05'
labels:
  - catalog
  - observability
  - design
dependencies:
  - JF-710
references:
  - >-
    backlog/tasks/jf-710 -
    JF-710-the-startup-skill-update-capture-overwrites-the-ledger-Error-null-erasing-the-JF-705-frozen-clause-under-the-skip-gated-restart.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-710 merge
(commit a09a0448, finding 3 of 5, the altitude finding). The per-locale ledger's Error
free-text field is now a wire protocol among THREE writers (the JF-705 PUT writer, the
JF-709 no-PUT writer, the JF-710 startup capture preserve), with each coordination
hazard fixed by layering another marker special case: the NoPutLedgerTail marker, then
the FrozenLedgerClauseMarker, then the PreviousLedgerDiagnosticPrefix framing literal,
then the two-Replace decomposer in PreserveLedgerErrorAcrossCapture. The JF-710 diff's
own six-shape trace shows the composition matrix every new writer must re-enumerate
against three Contains-matched literals; a miss compiles clean and surfaces only as a
corrupted or immortal diagnostic on a real box. The safety of the whole family rests
on the unstated invariant that only this subsystem's writers can ever put the three
literals into an Error (a richer canary quoting the clause, or a SMAPI build-error
message incidentally containing "; no PUT this run", would be silently preserved
forever or text-mutated).

THE WORK (a design task): add a structured, persisted caveat field to LocaleModelStatus
(an XmlSerializer-safe property beside Error, e.g. CatalogCaveat carrying the frozen
types and the no-PUT flag, or a small enum-plus-payload) so each writer sets its own
field instead of composing strings, the capture preserves by copying the field (no
parsing at all), and the config UI renders the caveat beside the free-text Error. This
deletes the parser family (IsOwnShapeLedgerError, PreserveLedgerErrorAcrossCapture's
Replaces, the three markers) and closes the invariant exposure by construction. Mind:
the persisted XML shape gains an additive field (the LastPlayedLaunchRoute compat
pattern); old persisted rows carry no caveat and must read as caveat-less with the
legacy Error text still rendered; config.html gains the rendering (the
embedded-resource clean-build dance applies); and the JF-705/JF-709/JF-710 pin families
migrate from string assertions to field assertions. Sequence AFTER JF-719 (the
IN_PROGRESS gate decision) lands or is decided, so the capture gate's final shape is
what the field design serves.
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
