---
id: JF-741
title: >-
  JF-741 - fold AudiobookPositionTracker.NormalizeKey into the shared
  GUID-to-N canonicalization rule (two copies of the same re-key rule)
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - playback
  - queue
  - cleanup
dependencies:
  - JF-738
references:
  - 'backlog/tasks/jf-738 - queued-membership-protection-in-the-bounded-map-trims-is-inert-maps-are-N-keyed-queue-ItemIds-are-dashed.md'
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
- [ ] #1 One shared single-string canonicalization rule; both NormalizeToMapKeyFormat and AudiobookPositionTracker.NormalizeKey consume it (no second copy of the parse-or-raw rule)
- [ ] #2 dotnet build passes with 0 errors, no new warnings
- [ ] #3 dotnet test passes both TFMs
- [ ] #4 /simplify + /code-review high passed
<!-- DOD:END -->
