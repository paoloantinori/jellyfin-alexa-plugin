---
id: JF-737
title: >-
  JF-737 - the JF-727 emptiness pre-check is the one unpinned per-type fact:
  no cheap pin exists for the conjunction's collection derivation
status: To Do
assignee: []
created_date: '2026-10-03 23:11'
labels:
  - catalog
  - structure-pins
dependencies:
  - JF-727
references:
  - >-
    backlog/tasks/jf-727 - JF-727-two-residual-per-type-hand-enumerations-outside-the-JF-706-711-wiring-tables-protection-the-item-sourcing-block-and-CatalogWiringGraft-ExtractWiring.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from JF-727's /code-review high round (finding 1 of 6, the
only one not applied or doc-claused; the round's other five were applied in the JF-727
diff itself). The JF-727 sourcing fold pinned the fetch
(FetchLibraryItems_HasExactlyOneCallSite_TheSourcingLoop, caller-bound to
SyncUserLibraryAsync's own state machine) and the counts
(SyncResultCountSetters_AreCalledOnlyByTheWiringTableLambdas, the JF-716 closure idiom
on the StoreCount row lambdas), but the emptiness pre-check
(`typeLegs.All(leg => leg.Items.Count == 0)` in LibrarySyncService.SyncUserLibraryAsync,
the early "No artists, albums or series found" return) has NO pin of either kind, even
though the JF-727 task file itself calls the missed conjunction term the BEHAVIORAL half
of the fold (a missed count is cosmetic).

The gap: a future edit can rewrite `typeLegs.All(...)` back into a hand three-term
conjunction over `typeLegs[0..2]` with every pin green, and a fourth synced type added
after that silently misses its conjunction term (the repo's exact "missed one" class): a
library holding only the fourth type's items takes the emptiness skip and never syncs.

Why it was NOT closed inside JF-727 (verified first-hand, not assumed):
- BEHAVIORAL pin impossible today: over the current three rows, All() and the hand
  three-term conjunction are behaviorally identical; the distinguishing library
  (fourth-type-only) cannot be constructed because typeLegs is a method-local table and
  CatalogType's sync scope is three members.
- STRUCTURAL pin disproportionate: the derivation's observable IL signature is a call to
  the GENERIC Enumerable.All, emitted as a MethodSpec (0x2B) token; IlCallScanner's
  TryResolveMethod deliberately skips non-MethodDef/MemberRef tables (JF-634's same-
  module comparability rule), so binding "MoveNext calls All" needs a MethodSpec-aware
  resolution with generic context, a scanner extension out of proportion to the hazard.
- The weak closure (wrap the check in a named helper and pin the helper's single call
  site) pins the call-through but not the body's derivation: a hand rewrite INSIDE the
  helper keeps that pin green. Not worth the indirection for no real coverage gain.

Candidate closures, in order of preference: (a) if IlCallScanner ever gains
MethodSpec/generic-call resolution for another pin, add "SyncUserLibraryAsync's MoveNext
calls Enumerable.All over the wiring table" here; (b) when a real fourth synced type
arrives (forcing the CatalogWiring/InjectCatalogReferences arity edits), add the
fourth-type-only-library behavioral pin in the same change, which permanently
discriminates the hand conjunction; (c) accept the residual risk with the current
coverage: the fetch pin catches the realistic partial regressions (any re-expansion to
hand fetches), the conjunction is a one-line collection read whose rewrite-back is an
active edit rather than a passive miss, and the table comment documents the contract.
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
