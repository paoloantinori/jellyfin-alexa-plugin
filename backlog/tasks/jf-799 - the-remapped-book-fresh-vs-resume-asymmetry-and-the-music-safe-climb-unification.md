---
id: JF-799
title: >-
  JF-799 - the remapped-book fresh-vs-resume asymmetry: the metadata-remap shape's
  fresh arm serves single items while its tracked resume concats the folder (and the
  music-safe unification prerequisite)
status: To Do
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies:
  - JF-794
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-794 /code-review high round (2026-10-06, finding 3), same-turn per
the review-recommendation rule. The review's explicit question - whether the
preserved pre-JF-794 asymmetry between the two builders' type gates is correct to
keep, should be unified, or filed - resolves to FILE, with the reasoning below.

THE ASYMMETRY (pre-existing, deliberately preserved by JF-794 and pinned by
`PlayBook_TrackedResume_AudioTypedRemapChapter_ResumesViaFolderConcat`): for a
METADATA-REMAPPED book (a book folder whose chapter rows are Audio-typed, not
AudioBook-typed; the JF-784 leg-3 row set the concat endpoint's MediaTypes=Audio
enumeration exists for), `BuildVideoAppAudioResponse` gates the concat on
`AudiobookItems.IsAudioBook(item)` so the fresh launch serves the chapter as a
SINGLE-ITEM video-audio stream (no whole-book seek bar, no queue continuation over
VideoApp), while `BuildAudiobookResumeResponse`'s climb is deliberately TYPE-AGNOSTIC
(`TryResolveVerifiedParentFolder`), so the same book's tracked resume serves the
WHOLE-BOOK folder concat with `?start=`. Same book, same feature flag, two arms, two
answers.

WHY NOT UNIFY NOW (the review's both-directions analysis):
- Unifying the FRESH arm to type-agnostic is UNSAFE today: every plain song sits
  directly inside its MusicAlbum (a Folder), so the ungated climb would album-concat
  every music launch through the audiobook URL/tracker machinery. A music-safe
  discriminator is a prerequisite (reject MusicAlbum/Playlist parents, or gate on the
  books library's CollectionType).
- Unifying the RESUME arm to AudioBook-gated deletes the seek-bar resume for exactly
  the JF-784 leg-3 row set the concat endpoint explicitly serves.

THE SHARPEST CONCRETE HAZARD TO PROBE (the review's finding, restated as the
acceptance evidence): for a remapped book the fresh arm only ever serves single-item
4s-segment streams, whose `RecordPositionProgress` writes are LEAF-RELATIVE (scaled)
under the folder key; the tracked resume then slices the BOOK concat timeline at that
leaf-relative value - the exact JF-567 "chapter-relative value slicing the book
timeline lands mid-chapter-1" class the tracked arm's tracker-first gate was built to
keep out. Whether this mis-slices in practice needs a device probe.

ACCEPTANCE CRITERIA (when picked up):
- Device probe first: build a remapped-book fixture in the test library (Audio-typed
  chapter rows under a book folder), listen flat to chapter N via a fresh ask, then
  "resume", and observe where the sliced concat lands (mid-chapter-1 vs the correct
  book position).
- Decide the unification direction WITH the music-safe discriminator prerequisite
  (fresh-arm type-agnostic requires rejecting MusicAlbum/Playlist parents or gating
  on the books library; resume-arm gating requires accepting the remap seek-bar loss).
- Extend or retire `PlayBook_TrackedResume_AudioTypedRemapChapter_ResumesViaFolderConcat`
  to match the decision.
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
