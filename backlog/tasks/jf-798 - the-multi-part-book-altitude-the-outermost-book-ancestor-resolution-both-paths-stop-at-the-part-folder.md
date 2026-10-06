---
id: JF-798
title: >-
  JF-798 - the multi-part book altitude: the outermost-book-ancestor resolution (both
  paths stop at the part folder today)
status: To Do
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
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
