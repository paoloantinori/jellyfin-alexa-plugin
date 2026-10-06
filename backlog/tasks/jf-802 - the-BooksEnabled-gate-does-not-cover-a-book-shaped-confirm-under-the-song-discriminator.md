---
id: JF-802
title: >-
  JF-802 - the BooksEnabled gate does not cover a book-shaped confirm under the
  song discriminator (FindSong can match a single-file AudioBook; the "yes" then
  plays it with books disabled)
status: To Do
assignee: []
created_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies:
  - JF-795
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-795 worker (2026-10-06), same-turn per the review-recommendation
rule, from the /code-review high round's finding 5 on the new confirm-side
BooksEnabled gate. JF-795 adopted the JF-611 podcast-confirm shape: the book
confirm's routing branch (`mediaType == MediaTypeAlbum &&
IsBookDisambiguationPayload(item)`) now answers the FeatureDisabled Tell when
BooksEnabled is off, so a stale book prompt cannot launch after an admin
disabled books.

The gap the reviewer located is structural and PRE-EXISTING: the gate lives on
the Album-typed dispatch arm only. A BOOK-shaped payload that arrives under a
different mediaType discriminator reaches its legacy arm ungated. The real
producer: Jellyfin types AudioBook as a DERIVATIVE of Audio (reflection-probed
10.11.8 and 12.0.0, the AudiobookItems.IsAudioBookOrChapter doc), so FindSong's
song search can match a SINGLE-FILE AudioBook; its disambiguation emits
MediaTypeSong, and the YesIntent song arm (PlaySong / CrossMedia's
BuildSingleSongResponse) plays it with no BooksEnabled check. The direct ask
never has this hole (PlayBookIntentHandler gates BooksEnabled at entry, before
the slot is even read).

Hoisting the gate above the mediaType dispatch in YesIntentHandler is the
reviewer's named fix but is WRONG at this altitude: it would gate
song/album/artist confirms (items that are not books) on a flag that must not
affect them. The correct fix needs to decide what the BOOK identity is on the
SONG-typed confirm arm (the IsAudioBookOrChapter ancestor walk already exists
for the end-of-book decision and could anchor it), and whether a confirmed
single-file book under FindSong should ROUTE to the book leg (the
confirm-must-match-ask rule would then extend there too) or merely be gated.
Scope the fix behind that decision; do not copy the Album-branch gate.

Filing note: the two candidate shapes are (a) gate-only, a BooksEnabled check
that fires when the confirmed song-typed item resolves as an audiobook, and
(b) route-to-book-leg, which would give single-file books found via FindSong
the resume/continuation machinery JF-795 just unified. Shape (b) is the deeper
fix and touches the song confirm's response shape, so it needs its own round
and device verification.
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
