---
id: JF-634
title: >-
  JF-634 - Hoist the IL-test scaffolding: the assembly walk (4 copies) and the
  by-name token collection (2 copies) into IlCallScanner
status: To Do
assignee: []
created_date: '2026-09-25 11:45'
labels:
  - tech-debt
  - tests
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-631 /simplify pass (2026-09-25, same-turn rule: the skip was justified with 'deserves its own task', so the task is filed in the same turn).

The IL-scanning test scaffolding has two cross-file duplication families that each gained another copy with JF-631's roster test:

1. The assembly walk (typeof(BaseHandler).Assembly.GetTypes() x DeclaredCallableMethods, filtering to the callable set) now exists as 4 private copies across WarmingGateCoverageTests, SessionQueueReaderRosterTests, AudioPlayerPlayConstructionRosterTests, and (verify) one more. Hoist as IlCallScanner.DeclaredMethods(assembly) or a shared helper beside it.

2. WriteTokens (by-name open-world token collection off a target Type) in the JF-631 roster test re-implements WarmingGateCoverageTests' inline gate-token enumeration (WarmingGateCoverageTests.cs:86-97 era), including a 3rd copy of the allDeclared BindingFlags const. Hoist as IlCallScanner.MethodTokens(type, name) and consume from both.

Mechanical test-infra change; full suite verifies. Fold naturally with any future third roster test.
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
