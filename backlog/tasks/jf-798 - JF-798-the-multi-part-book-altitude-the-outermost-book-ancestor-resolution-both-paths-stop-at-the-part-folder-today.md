---
id: JF-798
title: >-
  JF-798 - the multi-part book altitude: the outermost-book-ancestor resolution
  (both paths stop at the part folder today)
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-11 22:40'
labels:
  - bug
  - audiobooks
  - 'gate:fixture-permission'
dependencies:
  - JF-793
  - JF-794
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-794 round (2026-10-06), same-turn per the review-recommendation
rule: the gate-marker addendum on JF-794 (the JF-793 marker F3) ordered the
multi-part decision decided there, and the decision is FILE, with the census
evidence below.

THE SHAPE: a multi-part book laid out as `Book/Part1/*.mp3`, `Book/Part2/*.mp3`.
A chapter leaf's ParentId is the PART folder, and the ONE-LEVEL climb
(`AudiobookItems.TryResolveVerifiedParentFolder`, shared by the PlayBook-side
`TryResolveBookFolder` and the JF-794 VideoApp builders) resolves the PART
folder, not the book: head, confirm, continuation, and the concat URL all stop
at the part boundary. A user asking for the book gets one part per ask (whichever
part owns the matched chapter), and the seek bar spans the part, not the book.

THE DECISION EVIDENCE (the live census, recorded in JF-793; artifacts
`/tmp/jf793_audiobook_leaves.json` + `/tmp/jf793_parents.json`, script
`/tmp/jf793_census.py`, re-run during JF-794): 15 distinct parents of AudioBook
leaves; 14 are BOOK folders whose every child file sits DIRECTLY inside the
parent path (direct == kids on every row); the only parent with children one
directory deeper is the SHARED "Audiobooks" library container holding the 6
collapsed single-file books. NO multi-part book exists in the live library, so
the altitude walk would be entirely untested by reality on this install.

WHY FILED, NOT LANDED: the outermost-ancestor walk is NOT small and not provably
behavior-preserving. Jellyfin types nothing: the library container and a book
folder are both plain `Folder`, so the walk's stop condition (which ancestor is
"the book" vs a shared container) has no type evidence to read. Candidate
discriminators each need their own design round: the library-root/CollectionType
probe (a per-climb `GetItemById` chain to the collection root), folder-content
heuristics (does the candidate contain sibling subfolders with audio?), or a
name-affinity check. Flipping the altitude also moves the JF-793
chapter-granular disambiguation normalization, the tracker book keys, the
concat enumeration, and the continuation boundaries TOGETHER (the JF-794
addendum's "both paths flip together" requirement), each with its own red pins.

ACCEPTANCE CRITERIA (when picked up):
- Build a live multi-part fixture first (a real `Book/Part1`+`Part2` layout in the
  test library) and capture the observed behavior per path (head, disambiguation,
  confirm, resume, tracker key, seek-bar span) before designing the walk.
- The stop condition must discriminate the library container from a book folder
  WITHOUT weakening the JF-793 shared-container rejection (the 6 collapsed books
  must keep playing as their own tracks); red pins both directions.
- The flip must land on BOTH paths (the PlayBook-side resolution and the JF-794
  builders' verified climb) in ONE change, with the tracker-key coherence pin
  extended to the outermost id.
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
SUPERSEDED 2026-10-11 (permission granted): PARKED ON A PERMISSION GATE (2026-10-10 18:50, orchestrator): the task's own first acceptance criterion demands a LIVE multi-part fixture ('a real Book/Part1+Part2 layout in the test library') captured BEFORE designing the walk. No test library separate from the household install is known to exist, and creating Book/Part folders in the household's media library is a change to the maintainer's data layout, not an autonomous-lane decision. UNBLOCK: Paolo either points at a test library / blesses a scratch folder in the household library (a disposable 'ZZZ Test Book/Part1+2' the census can then re-run against), or the task stays parked. This is a user-input gate per the standing rules, not a weight call; everything else in the task is design-ready.
LANDED 2026-10-11 (commit 3aa9a77b on jf798, orchestrator merge; the combined-tree suite 5783/5783 both TFMs). LIVE CAPTURE (the first acceptance criterion, against the granted fixture ZZZ_DEVELOPMENT_TESTS/Multipart Book, read-only API probe): Part1/Part2 each resolve their OWN part today (the leaves' ParentId = the part folder); a multi-part book presents as two unrelated timelines (per-part seek bar, no cross-part resume, the ask yields whichever part owns the matched chapter). Two NEW census shapes recorded: 'Adam Grant - Think Again' (7 part-NAMED chapter FILES directly in the book folder, one-level already = book, the must-not-move direction) and the container's Book-typed/empty children. DISCRIMINATOR: name-affinity part markers (an anchored regex, marker word + required numeral, full-match; [GeneratedRegex]). THE WALK lives inside the ONE seam (TryResolveVerifiedParentFolder's scratchpad body, after the unchanged JF-793 one-level verdict): climb while part-like AND path-verified, hop cap 4; both paths flipped in ONE change because every consumer derives from the seam; the concat enumeration is Recursive so both parts follow at no further cost. PINS 13 both directions (6 direction-1 red-proven pre-change; 7 direction-2 incl. the marker-named-book-under-container climb pinned AS the accepted residual). The pre-JF-798 part-keyed tracker marks become dead data (the one-time upgrade note in CLAUDE.md, JF-794 doctrine; no real user marks could exist). The PARKED note below is superseded (the permission was granted 2026-10-11).
<!-- SECTION:NOTES:END -->
