---
id: JF-644
title: >-
  JF-644 - widen the slot-glue guard: halfwidth katakana + fullwidth forms + CJK
  Ext A ideographs (JF-638 review F1/F2), optional warning-level Latin-glue lint
  (F3)
status: To Do
assignee: []
created_date: '2026-09-27 07:22'
labels:
  - nlu
  - validator
  - hardening
  - ja-JP
dependencies:
  - JF-638
references:
  - >-
    backlog/tasks/jf-638 -
    Mechanical-guard-for-ja-JP-slot-glued-samples-third-live-SMAPI-build-failure-JF-513-JF-326-JF-636-catch-it-in-the-validator-generator-not-on-a-live-rebuild.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-638 code-review round (PASS verdict; these are the non-blocking hardening findings, landed as a task per the review-recommendation rule).

CONTEXT: JF-638 landed validator check #11 (error-level CJK slot-glue detection, scripts/validate_interaction_models.py `_is_cjk` + check in validate_single_model; harness tests/scripts/test_cjk_slot_glue.py). The reviewer verified the current class covers all three incident shapes and is false-positive-free on the real tree, and left these residuals:

1. HALFWIDTH/FULLWIDTH GAP (review F1): the ja-JP template header bans halfwidth katakana (U+FF66-FF9D) and fullwidth alphanumerics/punctuation (U+FF01-FF5E) from samples ANYWHERE, but that ban is comment-enforced only. A future glued sample using them (e.g. a halfwidth-katakana album name) passes the generator guards, passes Phase 5 regen-equality, passes check #11, and still fails the live SMAPI rebuild (InvalidSample/InvalidCharInSamples class). Widening the GLUE-ADJACENCY class to include FF66-FF9D and FF01-FF5E is strictly false-positive-free (glue-adjacent occurrences of banned-anyway characters are certainly wrong). Consider ALSO an anywhere-level FF-range scan for ja-JP samples (error-level, same false-positive-free argument: the characters are banned outright).
2. CJK EXTENSION A GAP (review F2): archaic kanji U+3400-U+4DBF are uncovered; a glued Ext-A ideograph passes today. Fix is one constant edit (extend the ideograph span from 0x4E00 down to 0x3400, staying contiguous) plus a harness case.
3. LATIN-GLUE DOOR (review F3, optional rider): CJK-only triggering assumes Latin glue is SMAPI-safe; no in-tree evidence either way. A WARNING-level non-CJK glue lint closes the visibility gap without CI risk (warnings never fail the job). Include only if trivial.

VERIFICATION BAR: harness cases for each widened class (glued halfwidth katakana, glued fullwidth form, glued Ext-A kanji all ERROR; the existing clean sweep over 17 models stays green and byte-identical findings); validator exit 0 on the real tree.

OUT OF SCOPE: renumbering or reseating check #11 (the reviewer confirmed the seat and the numbering); any generator-side duplicate of the check.
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
