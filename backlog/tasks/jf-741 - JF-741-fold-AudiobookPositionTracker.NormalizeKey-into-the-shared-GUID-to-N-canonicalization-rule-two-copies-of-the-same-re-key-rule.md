---
id: JF-741
title: >-
  JF-741 - fold AudiobookPositionTracker.NormalizeKey into the shared GUID-to-N
  canonicalization rule (two copies of the same re-key rule)
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-04 12:50'
labels:
  - playback
  - queue
  - cleanup
dependencies:
  - JF-738
references:
  - >-
    backlog/tasks/jf-738 -
    queued-membership-protection-in-the-bounded-map-trims-is-inert-maps-are-N-keyed-queue-ItemIds-are-dashed.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 from the JF-738 /simplify reuse round (the
review-recommendation rule: the finding is real, outside the shipped
change's surface, and lands in the tracker the same turn).

JF-738 introduced `DeviceQueueManager.NormalizeToMapKeyFormat`
(Alexa/Playback/DeviceQueueManager.cs), whose per-element rule

  Guid.TryParse(id, out parsed) ? parsed.ToString("N") : id

is character-for-character the same canonicalization rule as the
PRE-EXISTING private `AudiobookPositionTracker.NormalizeKey`
(Alexa/Playback/AudiobookPositionTracker.cs ~162-165:

  Guid.TryParse(bookParentId, out g) ? g.ToString("N") : bookParentId

), so the codebase now carries TWO independent copies of the
"parse-or-raw, re-key to N" rule. The JF-738 worker did NOT consolidate in
its own diff: AudiobookPositionTracker is outside that task's enumerated
surface (the trim machinery, the SetQueue callers' key formats, and their
tests), and the consolidation is a behavior-neutral refactor of
pre-JF-738 code, not a part of the membership fix.

COST OF LEAVING IT: silent divergence risk in exactly the key-format
bug class this repo documents as recurring (the CLAUDE.md
AudiobookPositionTracker gotcha "record (dashed URL) and read ("N") keys
silently mismatch"; JF-738 itself was a production instance of the same
class). If the canonicalization rule ever changes (case handling,
accepting brace formats, trimming), it must be discovered and applied in
both places.

RECOMMENDED SHAPE: expose a single-string core beside the enumerable
helper (e.g. `DeviceQueueManager.NormalizeToMapKeyFormat(string)` or a
small static `GuidKey.Normalize`), have `NormalizeToMapKeyFormat(IEnumerable<string>)`
delegate to it per element, and make `AudiobookPositionTracker.NormalizeKey`
delegate to it too. No behavior change anywhere; both call sites keep
their names. Verify with the existing
`NormalizeToMapKeyFormat_RekeysAnyGuidFormatToN_PassesNonGuidRaw` pin
(DeviceQueueManagerTests) plus the AudiobookPositionTracker suite.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 One shared single-string canonicalization rule; both NormalizeToMapKeyFormat and AudiobookPositionTracker.NormalizeKey consume it (no second copy of the parse-or-raw rule)
  Evidence: `DeviceQueueManager.NormalizeToMapKeyFormat(string)` is the ONE rule (DeviceQueueManager.cs); the IEnumerable overload delegates per element (`yield return NormalizeToMapKeyFormat(id)`) and `AudiobookPositionTracker.NormalizeKey` delegates outright; both former inline expressions are deleted. No third copy exists (whole-codebase sweeps by the reuse review agent and the code-review agent independently: every other `ToString("N")` site consumes an already-parsed Guid - parse-and-reject or Guid-typed writes, not parse-or-raw passthrough). The consolidation is held by TWO structural pins (the JF-737 inline-revert-loud precedent): `AudiobookPositionTrackerTests.NormalizeKey_DelegatesToSharedRule_NoInlineParseCopy_JF741` (zero Guid.TryParse call instructions anywhere on the tracker type INCLUDING compiler-generated nested types, and NormalizeKey still calls the shared rule) and `DeviceQueueManagerTests.NormalizeToMapKeyFormat_EnumerableDelegatesToCore_NoInlineParseCopy_JF741` (the enumerable overload plus its iterator state machine `<NormalizeToMapKeyFormat>d__N` carry no parse and do call the core). Both pins RED-PROVEN live on both TFMs (see Final Summary).
- [x] #2 dotnet build passes with 0 errors, no new warnings
  Evidence: `dotnet build Jellyfin.Plugin.AlexaSkill.sln --configuration Release --no-restore -warnaserror` on the final state: 0 warnings, 0 errors, both TFMs. One build-time fix inside the round: adding the string overload made the two pre-existing bare `<see cref="NormalizeToMapKeyFormat"/>` crefs ambiguous (CS0419); both are now qualified with parameter lists, as is the test-side cref the ambiguity also touched.
- [x] #3 dotnet test passes both TFMs
  Evidence: full suite ONCE on the final state: 5119/5119 net9.0 (1m39s) and 5119/5119 net10.0 (1m43s) - baseline 5116 + 3 new pins, zero failures, zero flakes. The filing's named verification both green: the JF-738 behavioral pin `NormalizeToMapKeyFormat_RekeysAnyGuidFormatToN_PassesNonGuidRaw` inside DeviceQueueManagerTests 74/74 both TFMs (it now transitively exercises the string core through the enumerable), and the AudiobookPositionTracker suite 13/13 both TFMs with every pre-existing test byte-identical (none of the 11 prior tracker tests was edited). Filtered runs re-ran green after every edit and sabotage round.
- [x] #4 /simplify + /code-review high passed
  /simplify (4 agents, reuse/efficiency/simplification/altitude): reuse CLEAN (the diff IS the dedup; no pre-existing helper missed); efficiency CLEAN (iterator laziness/allocation unchanged, no new hot-path work, callee a same-assembly static inline candidate); simplification 1 finding APPLIED (the "rule can only change in one place" rationale was stated twice inside the diff - the wrapper doc trimmed to end at the cref, the core doc owns the rationale); altitude 4 findings - F1 APPLIED as the tracker structural pin (the fold was enforced by prose only; a behaviorally identical re-inline passed the suite - the agent's own scenario "a one-line delegation is exactly what a future simplifier inlines"), F2 SKIPPED-no-change (home on DeviceQueueManager judged acceptable: sibling namespace, pure static call, no state coupling; the neutral GuidKey home "heavier than the coupling it removes"; revisit trigger recorded in the core's doc), F3 SKIPPED (the documentary shared-rule-read leg of the behavioral pin kept: its value IS naming the fold's invariant), F4 APPLIED (the test-side cref my overload made ambiguous). /code-review high (4 findings, no correctness bug - "the production delta is provably behavior-neutral"): CR-F1 APPLIED (the enumerable's delegation was the unpinned mirror of the tracker hole - the DQM-side structural pin added and RED-PROVEN); CR-F2 APPLIED (the tracker pin's leg 1 scanned only declared methods, so a lambda-shaped copy on a `<>c` display class escaped - leg 1 now walks the full nested-type closure via IlCallScanner.NestedTypeClosure (widened private-to-internal for exactly this), RED-PROVEN with a lambda-shaped sabotage caught at `<>c.<NormalizeKey>b__14_0`); CR-F3 SKIPPED with reasons (neutral-home relocation: the filing explicitly sanctions the overload home, the simplify-altitude round already examined and rejected the neutral home, and the pin reddening on a future move is the pins-by-design property; the third-consumer-outside-Playback trigger is recorded in the core's doc and both pins move with the rule in the same change); CR-F4 SKIPPED with reasons (widening the signature to `string?` cascades `!` suppressions through already-guarded call sites - RecordSegment's dictionary write, the enumerable's yield - for a null class no production path reaches; the non-nullable annotations deliberately match both pre-fold signatures, and the doc now states that posture explicitly so the runtime null-passthrough note cannot be read as a type-system contract). JF-748 went unused: no out-of-scope findings.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FOLD SHAPE (the filing's first alternative): a single-string core
`internal static string NormalizeToMapKeyFormat(string itemId)` on
DeviceQueueManager (the rule's senior owner: the JF-738 membership re-key and
the four "N"-keyed bounded maps it serves), placed beside the enumerable
helper; `NormalizeToMapKeyFormat(IEnumerable<string>)` delegates per element
(`yield return NormalizeToMapKeyFormat(id)`), keeping its iterator form and
name; `AudiobookPositionTracker.NormalizeKey` delegates outright, keeping the
tracker-domain name the CLAUDE.md record/read contract and the
PlaybackLaunchBuilder comment reference by name. Both former inline
parse-or-raw expressions are deleted. The neutral `GuidKey` home was
considered and declined (filing-sanctioned either way; a new Util type for
one expression outweighed by a sibling-namespace pure static call); the
revisit trigger (a third consumer outside Alexa/Playback) is recorded in the
core's doc, where both structural pins are named as moving with the rule.

SEMANTIC-DIFFERENCE AUDIT (pre-fold vs post-fold, every input class): the
tracker's `Guid.TryParse(bookParentId, out g) ? g.ToString("N") : bookParentId`
and the shared per-element `Guid.TryParse(id, out parsed) ? parsed.ToString("N") : id`
are the same expression modulo parameter/temp names (same static Guid.TryParse
overload, same "N" format, same passthrough of the input reference), so no
input class diverges: null (TryParse false, returns null both ways; RecordSegment
and GetPositionTicks guard IsNullOrEmpty before calling, Clear does not and its
TryRemove(null) ArgumentNullException is identical pre- and post-fold), empty
and whitespace (passthrough both), non-GUID text (raw passthrough both), every
GUID format D/N/B/P/X (re-key to lowercase "N" both), whitespace-padded GUID
(TryParse trims, re-keys both). Observable behavior is preserved exactly; the
filing's "No behavior change anywhere; both call sites keep their names" holds.

EQUIVALENCE PROOF (the fold changes nothing observable, proven not asserted):
Sabotage A restored the tracker's ORIGINAL inline expression verbatim and ran
the new behavioral pin `RecordReadKeyAgreement_GoesThroughSharedGuidToNRule_JF741`
(dashed record / "N" read / brace-format advance / "D" Clear / shared-rule-output
read key / non-GUID Ordinal distinctness) - GREEN on both TFMs under the
pre-fold code, i.e. the pin cannot distinguish the delegating tracker from the
pre-fold inline tracker on its whole input matrix. The pin's own coverage gap
note is part of the record: before JF-741 the tracker suite NEVER exercised the
GUID re-key arm (the "book1" fixtures are all non-GUID passthrough), so the
record/read key agreement the CLAUDE.md contract names had no tracker-side
signal on GUID inputs at all; the behavioral pin closes that too.

RED PROOFS (all live, both TFMs, exact failing bodies named): Sabotage B
(tracker returns input unchanged) reds the behavioral pin at the dashed-record/
"N"-read assert. Sabotage A rerun (behaviorally identical re-inline, the exact
shape a future /simplify pass would write) reds the tracker STRUCTURAL pin at
"NormalizeKey must not call Guid.TryParse" while the behavioral pin stays
green - the documented division of labor (behavioral pin catches harmful
divergence; structural pin holds the consolidation). Sabotage C (re-inline the
enumerable's parse) reds the DQM-side structural pin at
"MoveNext must not call Guid.TryParse" - proving the iterator state-machine
attribution the pin needed (its first cut scanned only the wrapper body and was
VACUOUS: the delegation call and any re-inline both compile onto
`<NormalizeToMapKeyFormat>d__N.MoveNext`; the pin was rewritten before it ever
shipped green-but-toothless). Sabotage D (lambda-shaped parse copy inside the
tracker, code-review CR-F2's escape class) reds the tracker pin at
`<>c.<NormalizeKey>b__14_0`, proving the nested-type-closure walk catches
compiler-generated shapes. All sabotages restored; zero SABOTAGE markers remain
in the tree (grep-verified).

Suite: 5119/5119 net9.0 and net10.0 on the final state (baseline 5116 + 3 new
pins: the behavioral equivalence pin and the two structural pins); Release
-warnaserror 0 warnings 0 errors both TFMs; filtered gate classes 13/13
(AudiobookPositionTrackerTests) and 74/74 (DeviceQueueManagerTests) both TFMs
re-run green after every round. Gates: /simplify (4 agents) and /code-review
high (4 findings) run as literal Skill calls with all dispositions itemized
under DoD #4; commits carry the Gates marker. JF-748 unused: no out-of-scope
findings to file.

CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit 25031e05, --no-ff; zero gate-marker findings - the reviewer independently reproduced both the behavioral and the structural sabotages across all eight angles), combined-tree suite 5126/5126 both TFMs, deployed in the batched post-closure deploy. The fold closes the JF-738 simplify round's finding; the revisit trigger (a third consumer outside Alexa/Playback) is recorded in the core's doc.
<!-- SECTION:FINAL_SUMMARY:END -->
