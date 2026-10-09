---
id: JF-793
title: >-
  JF-793 - the JF-791 residuals: the YesIntent confirm leg, the shared-container merge
  hazard, and the chapter-granular disambiguation
status: Done
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
- [x] #1 dotnet build passes with 0 errors (Release -warnaserror: 0 warnings, 0 errors, both TFMs)
- [x] #2 dotnet test passes (5401/5401 both TFMs, baseline 5391 + 10 pins)
- [x] #3 No new compiler warnings introduced (-warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute shape change; disambiguation state still rides the existing DisambiguationHelper.MatchInfo DTO)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model/locale/utterance change, the standing constraint was honored)
- [x] #7 E2E test added for new intent or handler logic (N/A as E2E: handler-level behavior pinned by 10 unit pins across YesIntentHandlerTests/PlayBookIntentHandlerTests/PlayBookResumeTests with red proofs; the worker does not deploy, so the live simulator/device leg belongs to the orchestrator's deploy round)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new speech; the constraint forbade locale changes)
- [x] #9 /simplify passed (4 parallel angles, 4 applied + 4 reasoned skips, recorded in the e9c64508 gate commit)
- [x] #10 /code-review high passed (4 findings, all applied with red proofs, in the 191b7878 gate commit)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-793 complete (worker branch, 7 commits on top of b6b50278; not merged, not deployed). Per-finding dispositions:

FINDING 1 (YesIntent PlayBook confirm leg, landed): TryResolveBookFolder adopted at the leg's entry, null shapes degrading to the single-file fallback. Red pin first on the unmodified tree both TFMs (26-chapter book, confirm the leaf: expected chapters[0].Id, got the leaf's id), mirroring the JF-791 head pin. In-task decision: the adoption does NOT cover the leg's missing QueueContinuation minting, and the /simplify altitude round added the missing RESUME axis (the leg launches chapter 1 with no FindResumeTrackIndex at all); both filed as JF-795, amended with the routing-branch preference (the head's minting state grew with the JF-793 continuation triplet, so replication would copy the whole flow).

FINDING 2 (shared-container merge hazard, PROBE FIRST, instance found): the live minix census (read-only; 383 AudioBook leaves grouped by ParentId; 14 parents fetched individually) found 13 book folders whose every child file sits DIRECTLY inside the parent path and ONE real shared container: the 'Audiobooks' library folder (/data/media/audiobook/Audiobooks, parentId=None) directly holding 6 COLLAPSED single-file books (The Honest Truth About Dishonesty, both HBR 10 Must Reads volumes, Managing Humans, Power Moves, Radical Candor), each an AudioBook leaf whose file sits one directory DEEPER than the container (the resolver hoists collapsed single-file directories above their own folder). The unconditional climb would have enumerated the container recursively, the WHOLE library as one queue. Discriminator: AudiobookItems.SitsDirectlyInside (dirname(leaf.Path) == folder.Path, trailing separators trimmed, case-insensitive; null/empty paths and bare filenames default to the climb, the doc-contract the code-review F3 guard restored). Red pins RED pre-fix with the exact merge signature (Assert.Single failure: the collection contained 3 items) on the head path AND the YesIntent confirm twin; companion accept-arm pin plus the JF-791 own-folder pin upgraded to carry its folder Path lock the non-overfire direction. The VideoApp builders' raw-ParentId concat twin FILED as JF-794 (tracker-key/token-mint coherence called out as the design risk).

FINDING 3 (chapter-granular disambiguation, landed AFTER finding 1 per the filing's caveat): NormalizeBookCandidates maps the candidate set through TryResolveBookFolder and dedups by folder id BEFORE the disambiguation consumers, always returning the replaced list (the code-review F2 probe-verified correction: the count-preserved pass-through left distinct books' leaves chapter-granular). Red pin RED pre-fix ('Expected: 2 / Actual: 3'). The climb stays as the tolerant safety net. The code-review F1 probe-verified routing bug (the folder-id payloads failed IsAudioBook and fell into the PlayAlbum arm: unpaged queue 26, no resume, plain AudioPlayer under NativeControlsForBooks) fixed with AudiobookItems.IsBookDisambiguationPayload (AudioBook leaves OR plain Folders; MusicAlbums stay on the album leg); red pin RED ('Expected: 5 / Actual: 26') then green. The finding-3 commit's 'Folder-id payloads now no-op the leg's climb' claim was false and is corrected in the gate commit (report-faithfully rule).

FINDING 4 (head-page-bounded resume, landed, bounded shape held): when page 1 yields no position and InitialPageHasMore says the book extends beyond the page, one unpaged fetch (the JF-784 concat-endpoint shape) re-runs the ONE resume decision (FindResumeTrackIndex) on the full list; deepIndex > 0 re-slices the page at the position-holding chapter (launch token, offset, queue) and rebases the continuation triplet (StartIndex = deepIndex + page count, TotalCount = the fetch-all list's count). Red pins: primary RED pre-fix (chapters[0] at 0:00 vs chapters[21] at its 10-minute offset), companion continuation-offset pin verified RED against the pre-fix handler after the fact (Expected 26 / Actual 5). Fully-played-deep and fresh books are covered by the same semantics; the /simplify round replaced the page re-scan with the by-construction (0, deepTicks) and dropped the dead count guard. Not covered and noted: the confirm leg has no resume at all (JF-795); the album head's AUDIO route has the same page-1-bounded scan, FILED as JF-796 after verification (AlbumPlayService ~664).

Gates and evidence: /simplify (4 angles: reuse clean; 4 applied, 4 reasoned skips) and code-review high (4 findings, 4 applied, 2 of them empirically probe-verified by the reviewer on this tree, probes reverted clean) both as literal Skill calls in the transcript, with apply commits. Suites: intermediate per-class runs on both TFMs after every leg (final touched-class state 60/60); ONE full-suite run at the final state 5401/5401 both TFMs (5391 baseline + 10 pins); Release -warnaserror 0 warnings 0 errors both TFMs. Files: Alexa/Handler/Intent/YesIntentHandler.cs, Alexa/Handler/Intent/PlayBookIntentHandler.cs, Alexa/Util/AudiobookItems.cs, Alexa/QueueContinuationFetcher.cs (doc only), and the three touched test classes. The task number reservation for follow-ups holds at JF-794/795/796.
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

FINDING 4 (added by the JF-791 gate-marker, same-turn): the head-page-bounded resume on the flat book path. With the folder resolved, FindResumeTrackIndex scans only the 5-item initial page (ResumeMath iterates the argument list), so UserData/ItemPositionState progress on chapter 22 of 26 is invisible and a fresh ask relaunches from chapter 1 at 0:00 (pre-fix, the same ask played the matched chapter at its position, then silence; the net outcome still improves, and mid-book resume keeps working through the resume intent ledger, but the fresh-ask deep-progress corner regressed). The album precedent is NOT liftable (JF-625 criterion 3 is the video-route tracker override under the album GUID); the flat path needs its own shape: a bounded position-holding-chapter resolution before paging (e.g. query the folder children for the max UserData last-played timestamp when page 1 yields no position, map it onto the queue by chapter id, and offset the continuation accordingly), with its own red pin (deep progress, fresh ask, expect the position-holding chapter to launch at its position). Sequencing note: land AFTER findings 1-3 (the confirm-leg climb and the container probe change which queries exist).
<!-- SECTION:NOTES:END -->
