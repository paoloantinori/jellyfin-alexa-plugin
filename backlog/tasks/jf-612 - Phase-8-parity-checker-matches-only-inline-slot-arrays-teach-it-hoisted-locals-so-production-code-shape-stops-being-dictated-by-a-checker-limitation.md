---
id: JF-612
title: >-
  Phase 8 parity checker matches only inline slot arrays; teach it hoisted
  locals so production code shape stops being dictated by a checker limitation
status: Done
assignee: []
created_date: '2026-09-20 21:22'
updated_date: '2026-09-21 08:38'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the /simplify altitude pass on commit 72b1989a: AddSongToPlaylistIntentHandler's two BuildElicitSlotResponse calls keep the slot array as an inline literal purely because scripts/validate_interaction_models.py Phase 8 (Shape A, lines ~740-751) matches only inline array literals; a future refactor that hoists the array into a local or constant SILENTLY skips the allSlotNames-vs-model parity check, which is the anti-pattern #9 silently-dropped-ElicitSlot class. The root-cause fix is checker-side: teach Phase 8 to resolve simple hoisted locals/constant fields (a bounded assignment-expression lookup within the same method/file), then hoist the AddSong arrays into a private static readonly and delete the inline-literal constraint comment. Acceptance: (1) Phase 8 parses a hoisted-local elicit call and enforces parity (test with a deliberately-wrong hoisted array fails validation); (2) the AddSong arrays hoisted, validator still passes; (3) the in-code comment updated or removed. Deferred from the remediation commit because the validator is a 700-line load-bearing script deserving its own change and tests.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-09-21 (commit 93f04811). The Phase 8 parity checker resolves hoisted allSlotNames declarations per file (cross-file collisions cannot silently satisfy a call), strips comments first, tolerates named-args-in-order and this.-qualified shapes, degrades unresolvable/multi-declared/token-free identifiers to warnings that now actually print (the check's warnings were discarded at the only call site - pre-existing bug fixed), never prints the all-clear over unverified parity, dedupes diagnostics, and covers the BuildDialogElicitResponse sibling loop. The AddSong arrays hoisted to AllSlots with the lifted-constraint rationale; AssertElicitsSlot asserts the updatedIntent slot set. Negative battery verified: wrong same-file array FAILS, named-args wrong array FAILS, doc-comment example harmless, same-name shadow WARNS, clean tree PASSES. Gates: literal /code-review high (10 findings: 8 applied, F6 partial rest filed in JF-613, F7 skipped millisecond-class) + literal /simplify; tests 4189/4189 both TFMs. The deep fix (C#-side slot-set table + validator pytest harness) is JF-613.
<!-- SECTION:FINAL_SUMMARY:END -->
