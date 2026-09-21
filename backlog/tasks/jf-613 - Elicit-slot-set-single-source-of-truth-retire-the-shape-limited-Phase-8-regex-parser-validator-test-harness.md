---
id: JF-613
title: >-
  Elicit slot-set single source of truth (retire the shape-limited Phase 8 regex
  parser) + validator test harness
status: To Do
assignee: []
created_date: '2026-09-21 08:29'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the /code-review high round on JF-612 (2026-09-21): the Phase 8 elicit-parity checker is now a hand-rolled shape-limited C# parser (four recognized call shapes + an identity-funnel guard), and every new C# idiom is a fresh false-negative escape the regexes cannot see (any-order named args beyond the tolerated in-order form, C# 12 collection expressions, FrozenSet, Concat-built arrays, base-class hoists). The root-cause fix: ONE C#-side source of truth - an intent-to-full-slot-set table (SlotMappings.cs or IntentNames.cs) that every elicit call site consumes (allSlotNames = ElicitSlots.For(intent)) and the validator resolves with a single declaration lookup, collapsing the four regex shapes and the Shape B guard into one check. Fold in: (a) a test harness for the validator script itself (a pytest that exercises check_elicit_dialog_registration against fixture handler sources - the wrong-array/shadow/comment/named-args battery currently exists only as manual probes); (b) the double file-read of the handler tree (the combined decl map and the hoisted scan each re-read all 86 files - read once into a list and join). Trigger: do this when a fifth call shape appears or the next silent-escape incident lands, whichever first.
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
