---
id: JF-791
title: >-
  JF-791 - PlayBook search can never return a multi-chapter book folder (AudioBook is a
  leaf type), so the default path plays ONE chapter then silence
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-672 gate-marker (2026-10-06), its live-probe finding 2, same-turn per
the review-recommendation rule. A pre-existing defect the JF-672 probe premise exposed;
NOT a JF-672 regression (the search shape predates it).

THE DEFECT: PlayBookIntentHandler's book search (~line 107) queries with
`IncludeItemTypes=BaseItemKind.AudioBook`, but Jellyfin NEVER types a multi-file book
folder as AudioBook: the AudioResolver (verified byte-identical at v10.11.8 and v12.2)
skips multi-file directory collapsing (`resolvedItem.Files.Count > 1 -> continue`), so a
multi-chapter book directory is a plain `Folder` and `AudioBook : Audio.Audio` entities
are always single-file LEAVES. Therefore `books[0]` is always a chapter leaf, the head
chapters query built on `books[0].Id` never enumerates the book, and the device plays the
one matched chapter then goes silent. The entire paged head/confirm/tail machinery
(JF-670/JF-673/JF-672's AudiobookChapterOrder) only executes in unit tests that
hand-construct Folder parents.

THREE-WAY EVIDENCE (all from the gate-marker, re-runnable):
1. Server source at v10.11.8 and v12.2: the AudioResolver multi-file skip is
   byte-identical; `AudioBook : Audio.Audio` is a leaf class.
2. The live census parents (the 14-15 book folders in the production library) are all
   Type=Folder, none AudioBook.
3. The deployed plugin, simulator PlayBook on the 12.2.0 box: "measure what matters"
   (26 tagged chapters) logs "checking resume ... with 1 tracks" and launches chapter
   #22 of 26 alone; "the upside of irrationality" (100 chapters) launches chapter 065
   alone. No continuation minted, no queue, no paging.

WHY THE FLAG-ON PATH WORKS: `NativeControlsForBooks` routes through the VideoApp concat
builders, and `BuildVideoAppAudioResponse`/`BuildAudiobookResumeResponse` climb
`item.ParentId` to find the book folder; the default AudioPlayer path never climbs.

FIX SHAPE: in the PlayBook search resolution, when the matched item is an AudioBook
LEAF with a non-empty ParentId, resolve the book folder from the ParentId (mirroring the
builder's climb) and run the existing paged machinery on the folder; keep the single-file
AudioBook leaf as its own track (the existing shape). Also decide the search query shape:
either add Folder-with-audio-children to the IncludeItemTypes candidates or keep the leaf
match plus the climb (the climb is the smaller change and matches the JF-361
single-file/leaf duality the handler already documents).

BLAST RADIUS NOTE: once the folder resolves, the paged path comes ALIVE on device for the
first time, which makes JF-790's untagged-order concern real on the default path (it is
currently unreachable there); JF-790 is effectively downstream of this fix. The JF-672
AudiobookChapterOrder already lands correct for the tagged class; the untagged class
needs JF-790's filename-sort adoption at the queue side.

VERIFICATION BAR: simulator PlayBook against a multi-chapter book on the deployed box
must log the full chapter count (not "with 1 tracks") and mint the continuation; a
red-proof pin first (the current shape's 1-track log is the failure signature); the
single-file leaf book keeps playing as its own track.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
      (DONE: Release -warnaserror on the full solution, 0 warnings 0 errors, both TFMs)
- [x] #2 dotnet test passes
      (DONE: 5391/5391 both TFMs, the 5387 baseline + the 4 new pins)
- [x] #3 No new compiler warnings introduced
      (DONE: the same Release -warnaserror run is clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
      (N/A: no session attribute changes; the diff touches no attribute writes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
      (N/A: no HttpClient changes; the only new I/O is one LibraryManager.GetItemById)
- [x] #6 NLU test fixtures updated if interaction model changed
      (N/A: no interaction model change; the fix is handler-side only)
- [x] #7 E2E test added for new intent or handler logic
      (DONE at unit level with the RED-proof pin family; the LIVE simulator leg is the
      orchestrator's post-deploy round, see the Final Summary's close-out bar: the
      deployed DLL must log the full chapter count, not "with 1 tracks", for PlayBook
      on "measure what matters". The simulator runs the deployed build, so it cannot
      run from this worker branch)
- [x] #8 Locale response strings added to all 17 locales
      (N/A: no new strings; the book name spoken on the multi-chapter path changes
      from the chapter's name to the folder's name through the existing strings)
- [x] #9 /simplify passed (no blocking cleanups remaining)
      (DONE: 4 parallel angles; 1 applied, the handler-site comment deduped onto the
      helper doc as the single rationale home; 1 filed, the chapter-granular
      disambiguation residual, JF-793 Finding 3; reuse and efficiency clean)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
      (DONE: 5 findings, all dispositioned same-turn. Applied: the RED pin upgraded to
      the real N-leaf entry shape through HandleFuzzyMiss (F2), the change-invariant
      own-folder single-file pin (F3), the helper doc's two divergence notes (F4/F5).
      Filed on JF-793: the YesIntent confirm-leg divergence (F1, Finding 1), the
      shared-container merge hazard sharpened with the pin-coverage and subfolder
      angles (F3/F4/F5, Finding 2). The climb itself survived every axis)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SHIPPED: the chapter-leaf climb. `AudiobookItems.TryResolveBookFolder(item, libraryManager)`
(the default AudioPlayer path's twin of the VideoApp builders' ParentId climb; returns the
resolved book FOLDER, null on the single-file empty-ParentId shape, on any non-AudioBook
match, and on a failed or non-Folder parent resolution) adopted at the ONE
post-disambiguation point in `PlayBookIntentHandler`, so the head chapters query, the
QueueContinuation ParentId, and every book-name announcement/log read run on the BOOK
FOLDER. The search itself was deliberately NOT widened (the filing's smaller-change
choice: widening IncludeItemTypes to Folder changes which items match other consumers).
Downstream flag-on arms were already folder-correct (the builders climb item.ParentId
themselves); the fix aligns the default path's QUEUE with what the flag-on concat
already played, and the JF-694 tracker key agreement is unchanged (both shapes key the
folder).

RED PROOF (unit level, both TFMs, run on the unmodified production tree twice: once for
the original single-leaf pin, once after the review upgraded it to the real entry
shape): `PlayBook_MultiChapterBook_ChapterLeafMatch_ClimbsToBookFolder_AndMintsContinuation`
mocks the live shape (a plain Folder book parent, 26 AudioBook chapter leaves, the
search returning TWO chapter leaves so the flow runs HandleFuzzyMiss's auto-accept arm
before the climb). On the unmodified tree it fails with the exact 1-track signature:
the AudioPlayer token is the matched chapter leaf's id, not chapter 0's (Expected
chapters[0] / Actual the leaf), and no continuation is minted; post-fix it plays the
book's first chapter, queues the head page (5), and mints the Audiobook continuation
(ParentId = the folder, TotalCount 26, StartIndex 5). The invariance run (base tree)
fails ONLY this pin: all three companion pins are green on both trees, pinning that the
single-file empty-ParentId book, the single-file book in its own folder, and the
climb-failure degrade all keep today's leaf play.

GATES: worker /simplify (4 parallel angles: 1 applied, 1 filed as JF-793 Finding 3,
reuse + efficiency clean) and /code-review high (5 findings: 3 applied, all 5 tracked
on JF-793; the climb survived every axis). Suites: touched classes 28/28 both TFMs;
full suite 5391/5391 both TFMs (5387 baseline + 4 pins); Release -warnaserror clean.

FILED (JF-793, the JF-791 residuals, same-turn per the review rule): the YesIntent
PlayBook confirm leg runs the chapters query on the confirmed chapter leaf (the same
one-chapter-then-silence on the "yes" path; the helper is placed for a one-line
adoption), the shared-container single-file merge hazard (needs the live census probe),
and the chapter-granular disambiguation (prompt lists chapters of the same book;
pre-normalization of the candidate set).

CLOSE-OUT BAR (the orchestrator's post-deploy leg, NOT runnable from this worker
branch: the simulator executes the DEPLOYED DLL): after this merge deploys, run the
simulator PlayBook on "measure what matters" (26 tagged chapters) and verify the log
reads the full chapter count, not "with 1 tracks", that the launch is chapter 1 of the
book (not chapter 22 alone), and that a continuation is minted; the single-file book
("the hobbit" class) must keep playing as its own track. The JF-790 untagged-order
concern becomes real on this path the moment the folder resolves (it was unreachable
on the default path before this fix).
<!-- SECTION:FINAL_SUMMARY:END -->
