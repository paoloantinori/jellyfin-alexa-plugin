---
id: JF-638
title: >-
  Mechanical guard for ja-JP slot-glued samples: third live SMAPI build failure
  (JF-513, JF-326, JF-636); catch it in the validator/generator, not on a live
  rebuild
status: Done
assignee: []
created_date: '2026-09-26 16:14'
updated_date: '2026-09-27 07:51'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/ja-JP.yaml
  - scripts/validate_interaction_models.py
  - scripts/generate_interaction_model.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Third occurrence of the same mistake class: a new ja-JP intent shipped with slots glued to surrounding text and only a live SMAPI build failure exposed it. Precedents: JF-513 `{musician}を再生` (InvalidSample, commit 09f0f162), JF-326 `星{star_rating}で` (InvalidCharInSamples, live 17-locale rebuild returned 16/1, commit a4475ff9), JF-636 `速度{speed}` (commit 5b403cf6). The rule is documented only as prose: the templates/ja-JP.yaml header invariant ("Samples use regular ASCII spaces around slot refs ... a slot glued to a particle with no space is rejected") plus per-intent comments at RateItemIntent (line ~355) and SetPlaybackSpeedIntent (line ~448). Neither scripts/validate_interaction_models.py nor scripts/generate_interaction_model.py has any glue check (grep-verified 2026-09-26). Work order: add a mechanical check that flags any ja-JP sample where a slot ref `{...}` is not separated by a regular ASCII space (U+0020) from adjacent non-space characters on each side (both CJK-before `速度{speed}` and particle-after `{speed}にして` shapes; also catch adjacent slot refs `}{`). Two candidate seats, pick deliberately and justify in the task notes: a generator template guard (fails at authoring time, matching the existing guard family in generate_interaction_model.py) and/or a validator check in validate_interaction_models.py (decide error vs warning level explicitly; note the CI validate-models job fails on errors only, and check #10 stays warning BY DESIGN per CLAUDE.md). Guard must not fire on the current tree: all 17 committed models validate clean today (verified).
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

## Implementation notes (2026-09-27)

Python-only change; the dotnet DoD items above do not apply (no C#, no model JSON or YAML edits). Commit 9bbbc7e3 on worktree branch worktree-agent-a20ccd4a2a321ab9e.

- **Check number and seat**: check #11, ERROR-level, inside `validate_single_model` in `scripts/validate_interaction_models.py` (continues the file's inline numbering; CLAUDE.md's referenced #6 and #10 stay as they were). Applies to EVERY locale's samples, deliberately not gated on ja-JP: the other 16 locales carry no CJK characters in samples, so a locale list would only rot while the scan is a no-op there. Seat decision (the task asked for a deliberate pick): the validator seat, not a generator template guard. The CI validate-models job (blocking since JF-556) runs the validator on every push/PR against the committed JSON that SMAPI actually consumes, and Phase 5 regen-equality already pins committed JSON to the template byte-identically, so this one check covers both the hand-edit path and the template-drift path; a generator guard would fire only at regen time and is never run by CI.
- **Trigger condition**: in any sample, the character immediately before `{` or immediately after `}` of a slot placeholder (shared `SLOT_PLACEHOLDER_RE`) is in the CJK class. Only a regular ASCII space (U+0020) may separate. Error message names locale, intent, full sample, offending codepoints and both sides when both are glued.
- **Character ranges** (inlined in `_is_cjk` since the simplify round; the original four-entry table `CJK_ADJACENT_RANGES` was collapsed to the same class as two contiguous spans): U+3000-U+30FF (CJK Symbols and Punctuation incl. U+3000, Hiragana, Katakana incl. the prolonged-sound mark U+30FC) and U+4E00-U+9FFF (CJK Unified Ideographs). U+3000 (ideographic space) is inside the class ON PURPOSE: the ja-JP template header bans it in samples entirely, so a U+3000 "separator" must flag, not pass. Deliberately out of scope: halfwidth katakana U+FF66-FF9D and fullwidth forms U+FF01-FF5E (banned in ja-JP samples wholesale by the same header, but not part of the directed CJK class; widening them in is false-positive-free hardening, filed as the JF-644 follow-up) and adjacent slot refs `}{` (ASCII adjacency; the backlog sketch mentioned it, the implemented trigger is CJK adjacency only).
- **Harness/proof**: `validate_single_model` was already a pure importable function, so no refactor was needed; the harness is `tests/scripts/test_cjk_slot_glue.py` (10 tests, pattern follows the existing `tests/scripts/test_elicit_checker.py`): the three incident shapes verbatim (JF-513 `{musician}を再生して`, JF-326 `星{star_rating}で評価して`, JF-636 `速度{speed}にして`), one shape per CJK block, the U+3000 case, error-message attribution, spaced forms clean, `{album}{musician}` ASCII adjacency clean, Latin glue out of class, and a clean sweep over all 17 committed models. One test expectation was corrected during authoring against `unicodedata` (katakana TO is U+30C8, not U+30C6).
- **Clean direction**: `python3 scripts/validate_interaction_models.py` on the untouched models = `PASS: All interaction models are structurally valid (278 warnings...)`, exit 0, and the full output is byte-identical to the pre-change validator (old exit 0 vs new exit 0, `FULL OUTPUT IDENTICAL: True`), so zero new findings of either kind.
- **Dirty direction (CLI level)**: ran the real `main()` against a doctored ja-JP model copy in /tmp (injected ` {musician}を再生` into PlayArtistSongsIntent; no repo file touched). Tail:
  `FAIL: 1 error(s) found:` / `  [ja-JP] Intent 'PlayArtistSongsIntent': sample ' {musician}を再生' glues a slot to CJK text (U+3092 directly after '}'); separate with a regular ASCII space (SMAPI rejects the build; JF-638)` / `DIRTY RUN EXIT = 1`.
- **Sibling validators**: `validate_locales.py` PASS (no new locale gaps, exit 0); `validate_versions.py` PASS (1.0.0.0 across all three sources, exit 0). pytest `tests/scripts/` = 16 passed (10 new + 6 pre-existing elicit-checker).

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-27 as merge ff77804b (CI green, run 36303022981): validator check #11 (error-level CJK slot-glue detection in validate_single_model, two contiguous ord spans U+3000-30FF + U+4E00-9FFF) + the 10-case pytest harness tests/scripts/test_cjk_slot_glue.py (reusing test_elicit_checker's cached _models loader). All four /simplify angles ran: three findings applied (span collapse, dead types scaffolding dropped, loader dedup), two skips recorded (loop-merge: numbered-check convention + efficiency no-restructure; pad-and-index: equal forms). Code-review high PASS with independent execution of all five priorities (clean sweep two ways, old-vs-new byte-identical, dirty-direction on the real model, SMAPI payload identity verified through ModelDeploymentManager). Review residuals landed same-turn: JF-644 filed (FF-range + Ext-A widening), the InvalidSample comment nit and the stale-symbol task-note nit fixed in the branch. Validator output on the real tree byte-identical pre/post; harness 16/16. This closes the third-incident class: the next glued ja sample now fails CI before SMAPI ever sees it.
<!-- SECTION:FINAL_SUMMARY:END -->
