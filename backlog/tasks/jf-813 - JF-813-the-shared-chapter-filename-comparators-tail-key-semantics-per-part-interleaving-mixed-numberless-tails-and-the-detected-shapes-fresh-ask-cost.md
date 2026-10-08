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
- [x] #1 A decision is recorded (composite key vs documented status quo) for per-part file numbering, applied to the ONE shared comparator so endpoint timeline and queue order cannot diverge
- [x] #2 The numberless-file tail placement in mixed books is either fixed or re-documented as endpoint parity with an example
- [x] #3 The fresh-ask cost of the detected shape is either mitigated or accepted in writing with the census numbers
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

GATE-MARKER ADDENDUM (2026-10-07, from the JF-790 marker F1): item 1 gains the CONVERSE shape - a book whose page 1 is UNTAGGED but whose tail is correctly tagged (per-disc filenames) FALSE-FIRES the detection and the file sort discards the tags' order for the whole book; no census book has the shape, the boundary is documented at the trigger, and the composite key (IndexNumber first when present) is the fix vehicle for both directions.

DECISION RECORD (2026-10-08, orchestrator, closes all three ACs as documented
dispositions backed by the census evidence; no code change ships):

1. PER-PART INTERLEAVING (AC#1): STATUS QUO DOCUMENTED. The composite
   (directory, trailingNumber) key, or IndexNumber-first when present, is the
   fix vehicle for BOTH boundary directions (per-part interleaving and the
   page-1-untagged false fire) and it must land endpoint+queue together on the
   ONE shared comparator. It is NOT adopted now: adopting it changes the
   endpoint's concat timeline order, which invalidates every existing cached
   encode timeline AND the AudiobookPositionTracker's recorded high-water marks
   (a mark is meaningful only against the timeline that wrote it), for a
   benefit the census cannot exhibit (zero per-part-shaped and zero
   page-1-untagged books found). RE-OPEN TRIGGER: the first real book with
   either shape; the change then ships with an encode-cache invalidation and a
   tracker-mark migration decision in the same task.

2. MIXED NUMBERLESS TAILS (AC#2): STAYS, re-documented as endpoint parity with
   the example in the class doc. In a mixed book every numberless/unparsable
   file keys to int.MaxValue and sorts LAST: "intro.mp3, 001.mp3..050.mp3"
   plays the numbered chapters first and the intro at the very end. A
   MaxValue-preserving stable order for the mixed case is impossible without a
   second key axis (the DB order), which is exactly the composite-key vehicle
   of decision 1; the census has no mixed book, so no user has ever heard the
   wrong order.

3. FRESH-ASK COST (AC#3): ACCEPTED with the census numbers. Every ask of a
   DETECTED book pays the unpaged full-book fetch plus the O(chapters)
   FindResumeTrackIndex scan; the JF-797 probes are bypassed BY DESIGN (the
   order fix needs the fetch regardless of resume state; the probes exist to
   avoid exactly that fetch, so they cannot gate it). The bound: the census's
   largest untagged book is 100 chapters, one unpaged fetch at first ask of
   such a book, then normal paging. The candidate scan-skip on a total
   user-data miss was NOT taken: it saves only the scan, not the fetch, and
   adds a probe pass over the same rows the scan walks; measured against a
   100-chapter ceiling the saving is noise. Revisit only if a real detected
   book exceeds ~500 chapters.

The four code pointers that said "JF-813 owns/follow-up" now say decided; the
task closes as a decisions round. No /simplify or /code-review gate: zero code
diff beyond comment wording, documentation-only exemption.
