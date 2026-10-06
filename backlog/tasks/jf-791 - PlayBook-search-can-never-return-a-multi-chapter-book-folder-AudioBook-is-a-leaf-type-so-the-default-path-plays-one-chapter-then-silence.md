---
id: JF-791
title: >-
  JF-791 - PlayBook search can never return a multi-chapter book folder (AudioBook is a
  leaf type), so the default path plays ONE chapter then silence
status: In Progress
assignee: []
created_date: '2026-10-06'
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
