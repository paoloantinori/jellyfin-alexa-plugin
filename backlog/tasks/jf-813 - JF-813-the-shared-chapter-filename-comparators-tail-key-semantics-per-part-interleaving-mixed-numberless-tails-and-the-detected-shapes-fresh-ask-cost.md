---
id: JF-813
title: >-
  JF-813: the shared chapter filename comparator's tail-key semantics (per-part
  interleaving, mixed numberless tails) and the detected shape's fresh-ask cost
status: To Do
assignee: []
created_date: '2026-10-07 12:17'
labels:
  - audiobook
  - follow-up
milestone: m-18
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-790 code-review (high) round, same turn. Three residuals of the ONE shared trailing-filename comparator (ChapterFileNameOrder, extracted from the concat endpoint by JF-790 and now consumed by BOTH the concat timeline and the default paged queue), plus one cost note:

1. TAIL-KEY ONLY (the JF-625-era endpoint semantics, now queue semantics too): the comparator keys on the filename's trailing number alone. A multi-part book with PER-PART file numbering (Part1/01..12.mp3, Part2/01..12.mp3) interleaves parts (1,1,2,2,...) once the JF-790 detection fires (untagged class and/or within-part ties). A (directory, trailingNumber) composite key would preserve part grouping, but it changes the ENDPOINT's concat timeline order (pre-JF-790 behavior), which invalidates existing cached-encode timelines and tracker positions: the decision is endpoint+queue together, never queue-only (the ONE-definition rule).

2. MIXED numberless files: all non-parsing keys are int.MaxValue, so in a mixed book a numberless intro/cover sorts LAST even when the DB order had it first. The JF-790 doc records this as endpoint parity; decide whether it deserves a fix (e.g. MaxValue-preserving stability is impossible for mixed: pick and document) or stays.

3. FRESH-ASK COST: every ask of a detected (untagged/tie) book pays the unpaged full-book fetch plus the O(chapters) FindResumeTrackIndex scan, including cold first-ever asks the paged path's JF-797 probes deliberately keep off the deep scan. Bypassed by design (the ORDER needs the fetch regardless of resume state); census max untagged book = 100 chapters. Candidate: run the IsPlayed/IsResumable probes FIRST and, on a total miss, scan only... nothing (the order still needs the fetch, only the scan could be skipped: scan cost without any user data anywhere is provably the fresh-launch shape; verify FindResumeTrackIndex's early shapes against that proof before optimizing).

4. Detection reads the INITIAL page only (JF-790 prescribed shape): a book whose page 1 is tagged with distinct keys but whose later pages are untagged/tied escapes the fix. No census book has that shape today.

Constraints carried over from JF-790: ONE comparator definition, endpoint byte-identity, no locale/model/speech changes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A decision is recorded (composite key vs documented status quo) for per-part file numbering, applied to the ONE shared comparator so endpoint timeline and queue order cannot diverge
- [ ] #2 The numberless-file tail placement in mixed books is either fixed or re-documented as endpoint parity with an example
- [ ] #3 The fresh-ask cost of the detected shape is either mitigated or accepted in writing with the census numbers
<!-- AC:END -->

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
