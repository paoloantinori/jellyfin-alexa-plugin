---
id: JF-793
title: >-
  JF-793 - the JF-791 residuals: the YesIntent confirm leg, the shared-container merge
  hazard, and the chapter-granular disambiguation
status: In Progress
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies:
  - JF-791
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-791 implementation round (2026-10-06), same-turn per the
review-recommendation rule (Finding 3 from that round's /simplify altitude pass). All
three findings live outside JF-791's dispatched surface (PlayBookIntentHandler + its
handler tests + AudiobookItems); each needs its own red proof.

## Finding 1: the YesIntent PlayBook confirm leg has the same leaf-shape defect JF-791 fixed on the head path

`YesIntentHandler.PlayBook` (the JF-361 confirm leg) resolves the confirmed
disambiguation match via `GetItemById` and runs
`QueueContinuationFetcher.BuildScopedAudiobookChaptersQuery(book.Id, ...)` on the
confirmed item's OWN Id. PlayBook's disambiguation stores CHAPTER leaves (the AudioBook
search shape JF-791 documented: Jellyfin never types a multi-file book folder as
AudioBook), so a "yes" confirm on a multi-chapter book enumerates zero children and the
leg's single-file fallback plays the ONE confirmed chapter then silence: the same defect,
on the confirm path (reachable whenever the book search yields multiple sub-90-score
matches and the user confirms one).

Fix shape: adopt `AudiobookItems.TryResolveBookFolder` at the leg's entry (the helper
was placed in AudiobookItems, beside `IsAudioBook`, exactly for this adoption;
`_libraryManager` is already a field there, so the adoption is a one-line `is { }`
reassignment byte-similar in shape to PlayBookIntentHandler's); red pin in
YesIntentHandlerTests with the chapter-leaf shape, mirroring JF-791's
`PlayBook_MultiChapterBook_ChapterLeafMatch_ClimbsToBookFolder_AndMintsContinuation`.
Decide in-task: the confirm leg never mints a QueueContinuation at all (a pre-existing
gap vs the head path's paged machinery; long confirm-queued books truncate at the
initial fetch size) whether the adoption covers that or it needs its own filing.

## Finding 3 (from the JF-791 /simplify altitude round): the disambiguation still presents chapter-granular choices for a book-granular intent

JF-791's climb normalizes AFTER the candidate-set consumers: a title search on a
multi-chapter book returns N chapter leaves, and the multi-match disambiguation still
presents chapter-granular choices ("... - Chapter 22" vs "... - Chapter 5" of the SAME
book) whose confirm payload is a chapter leaf Id; whichever is picked, the climb then
plays the whole book from chapter 1, so the prompt offers an illusory choice (and the
"- Chapter N" name tails drag fuzzy scores below the >=90 auto-play bar the folder name
would clear exactly). Deeper shape: map the candidate list through
`TryResolveBookFolder` and dedup by folder Id BEFORE `HandleFuzzyMiss` (single-file
books pass through unchanged; ~3 lines at PlayBookIntentHandler's candidate-set
construction), keeping the post-disambiguation climb as the tolerant safety net (on a
Folder it harmlessly returns null). CAVEAT if this lands FIRST: the YesIntent confirm
leg then starts receiving Folder-shaped ids, so Finding 1's climb there becomes a
robustness net rather than the load-bearing fix; land Finding 1 before or together with
this so the leg never depends on payload shape.

## Finding 2 (needs a live probe before any fix): the unconditional parent adoption merges sibling single-file books in shared containers

`TryResolveBookFolder` adopts the parent Folder unconditionally, mirroring the flag-on
builders (`BuildVideoAppAudioResponse` concats `item.ParentId` unconditionally). For a
collapsed single-file book (the resolver collapses single-file directories; the AudioBook
leaf IS the book) whose ParentId is a SHARED container (library root, author folder),
the paged machinery now enumerates the container's audio children, merging sibling
single-file books into one queue. The flag-on concat path has ALWAYS had these
semantics, so the default path is now CONSISTENT with it rather than newly wrong; the
open question is only whether any real library hits the shape.

The discriminating probe (a census pass on the live box, or folded into JF-791's
post-deploy simulator round): group AudioBook leaves by ParentId and flag parents whose
children are NOT chapters of one book (children whose names/albums diverge from the
parent, or a parent that also contains non-audiobook children). If the shape exists, the
discriminator needs its own design pass; name-based checks were rejected in JF-791
because untagged books whose folder names diverge from the spoken query would fall back
to the 1-track bug the climb exists to fix.

Two sharpened angles from the JF-791 /code-review high round that this design pass must
carry (2026-10-06):

- The pin-coverage gap is half-closed in JF-791: the empty-ParentId and the
  own-folder single-file layouts are pinned, but the shared-container merge shape is
  deliberately NOT pinned as expected behavior, so this task's fix will need its own
  red pin for that layout (the pre-fix behavior there IS the merge).
- The climb's Folder-verification diverges from the VideoApp twin
  (BuildVideoAppAudioResponse concats the raw ParentId with no resolution check), and
  the climb is ONE level on both paths: a chapter under a subfolder
  (book/part/chapter) resolves the SUBFOLDER, truncating the book at the part
  boundary on both the paged and the concat paths. Any predicate change made here
  must decide whether the builders adopt it too (the documented twin equivalence
  otherwise drifts silently).
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

FINDING 4 (added by the JF-791 gate-marker, same-turn): the head-page-bounded resume on the flat book path. With the folder resolved, FindResumeTrackIndex scans only the 5-item initial page (ResumeMath iterates the argument list), so UserData/ItemPositionState progress on chapter 22 of 26 is invisible and a fresh ask relaunches from chapter 1 at 0:00 (pre-fix, the same ask played the matched chapter at its position, then silence; the net outcome still improves, and mid-book resume keeps working through the resume intent ledger, but the fresh-ask deep-progress corner regressed). The album precedent is NOT liftable (JF-625 criterion 3 is the video-route tracker override under the album GUID); the flat path needs its own shape: a bounded position-holding-chapter resolution before paging (e.g. query the folder children for the max UserData last-played timestamp when page 1 yields no position, map it onto the queue by chapter id, and offset the continuation accordingly), with its own red pin (deep progress, fresh ask, expect the position-holding chapter to launch at its position). Sequencing note: land AFTER findings 1-3 (the confirm-leg climb and the container probe change which queries exist).
