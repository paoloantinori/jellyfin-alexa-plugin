---
id: JF-644
title: >-
  JF-644 - widen the slot-glue guard: halfwidth katakana + fullwidth forms + CJK
  Ext A ideographs (JF-638 review F1/F2), optional warning-level Latin-glue lint
  (F3)
status: Done
assignee: []
created_date: '2026-09-27 07:22'
updated_date: '2026-09-28 22:25'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
["Landed on worker worktree branch worktree-agent-a759381961a698eb8, commit 30f66fca (not pushed; orchestrator merges). Python-only: scripts/validate_interaction_models.py + tests/scripts/test_cjk_slot_glue.py.", "F1: halfwidth katakana U+FF66-FF9D and fullwidth forms U+FF01-FF5E joined the check #11 glue-adjacency class via a shared _is_banned_ff helper (the two spans named once); glue-adjacent occurrences of banned-anyway characters are certainly wrong. F2: ideograph span floor dropped 0x4E00 -> 0x3400, contiguous over Extension A (the U+4DC0-4DFF Yijing gap rides along by the contiguity the task mandated; no in-tree occurrence).", "F1's 'consider ALSO' rider TAKEN: anywhere-level scan for the two FF ranges, error-level with its own JF-644 marker, folded INSIDE check #11 (no reseating/renumbering, per the task's out-of-scope line) and UNGATED across locales, reusing the check's existing no-locale-gate argument: probe of all 17 committed models found ZERO new-class characters anywhere in samples (glue-adjacent or not), so the scan is a no-op on the real tree and a ja-JP gate would only rot. A glued FF char reports twice (JF-638 glue + JF-644 anywhere ban), pinned by a harness case; the glue error's spacing advice would be wrong for an outright-banned char, so the JF-644 error carries the rewrite advice.", "Verification bar met: harness 17/17 in test_cjk_slot_glue.py (7 new pins; all three glued classes ERROR with the exact U+XXXX wording; span-boundary controls U+FF65/U+FF9F/U+FF5F and U+33FF stay clean, pinning the exact spans); full tests/scripts 23/23 with PYTEST_DISABLE_PLUGIN_AUTOLOAD=1. BOTH validators (validate_interaction_models.py, validate_locales.py) clean-run byte-identical pre/post change: sha256 f49a05cc... / c43f2119... match, exit 0 both. Sensitivity check: widening neutered in a live module copy -> all new pins go silent (0 errors), the four historical controls (JF-513/JF-326/JF-636/katakana-punctuation) still fire. 17-model sweep assert extended to cover the JF-644 marker.", "Skipped: the F3 Latin-glue WARNING lint (optional rider, not in the dispatch work list; a warning-emitting check is also structurally at odds with this change's byte-identity bar unless the tree is Latin-glue-free, and it deserves its own false-positive sweep before it ships); generator-side duplicate (out of scope per task); ja-JP-gated shape for the anywhere scan (ungated chosen, same no-op fact, avoids the gate the check's own comment argues against); C# DoD battery inapplicable (no C# surface touched; gate-exempt trivial justification in the commit message, precedent 2c40a60b). Status left To Do for the orchestrator's DONE transition."]
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as merge bd39abe7 (pushed; python-only, no deploy): the glue-guard widening. The adjacency class now covers halfwidth katakana U+FF66-FF9D and fullwidth forms U+FF01-FF5E (single-sourced in _is_banned_ff, shared with the new anywhere-level ban on both FF ranges, which the ja template header bans outright with the live InvalidCharInSamples precedent), and the ideograph span extends down through Extension A (contiguous). Byte-identity proven at sha256 level on both validators; 7 new bar-sensitive pins (the neuter-check leaves the historical controls firing); the real tree is zero-occurrence for the new classes; the F3 Latin-glue warning lint is noted for its own change (a warning emitter would have broken the byte-identity bar). Verification: the worker's battery plus my independent run (harness 23/23, validator exit 0, identical findings).
<!-- SECTION:FINAL_SUMMARY:END -->
