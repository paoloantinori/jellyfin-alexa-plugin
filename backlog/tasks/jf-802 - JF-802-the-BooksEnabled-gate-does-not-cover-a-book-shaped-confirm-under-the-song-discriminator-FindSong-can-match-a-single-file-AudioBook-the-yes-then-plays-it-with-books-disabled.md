---
id: JF-802
title: >-
  JF-802 - the BooksEnabled gate does not cover a book-shaped confirm under the
  song discriminator (FindSong can match a single-file AudioBook; the "yes" then
  plays it with books disabled)
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-07 08:42'
labels:
  - bug
  - audiobooks
milestone: m-18
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute changes)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model changes)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; the worktree does not deploy, the E2E axis is covered by the new unit pins)
- [x] #8 Locale response strings added to all 17 locales (N/A: reuses the existing FeatureDisabled string, no new strings)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary
<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed by the JF-802 worker (2026-10-07), shape (a) gate-only ONLY as the
filing scoped: inside YesIntentHandler's song arm, before the music gate, a
BooksEnabled check fires when the confirmed song-typed item resolves as
book-shaped through AudiobookItems.IsAudioBookOrChapter (the filing's named
anchor, the JF-670 end-of-book walk; it also catches AudioBook-typed chapter
leaves via the walk's hop-1 type test). The flag read comes FIRST so the
ancestor walk runs only on the books-disabled path (plain songs pay zero
lookups), and the refusal reuses IfFeatureDisabled(c => c.BooksEnabled,
request), the identical call PlayBookIntentHandler makes at its entry gate, so
the Tell is byte-identical to the ask's by construction. Books-first ordering
on a book-shaped item means the both-flags-off intersection answers
FeatureDisabled like the book ask (pinned, code-review F4). Shape (b), routing
the confirmed single-file book to the book leg's resume/continuation machinery,
REMAINS the deeper follow-up (the filing already carries it; no new task filed)
and its response-shape change still needs device verification: with books ON
the song arm still launches the item through BuildSingleSongResponse, and a pin
locks that the gate does not silently take that route.

RED PROOF (both TFMs, on the unmodified tree):
YesIntentHandlerTests.HandleAsync_DisambiguationSongType_BookShapedConfirm_
BooksDisabled_AnswersFeatureDisabled - a single-file AudioBook served as the
MediaTypeSong disambiguation payload, BooksEnabled off; pre-fix FAILED with
"the disabled Tell must carry no directives" (the confirm LAUNCHED the book
through BuildSingleSongResponse: directive present, queue and
FullNowPlayingItem written), the exact hole. Post-fix the confirm answers the
FeatureDisabled Tell, no directives, no queue state.

Companion pins: a plain song confirm plays whatever the books flag says
(Theory, books on and off: books off must not touch songs); a book-shaped
confirm with books ENABLED still launches through the song arm (the gate-only
boundary); the both-flags-off ordering pin (code-review F4: FeatureDisabled,
never MediaTypeNotAvailable); the pre-existing JF-806 music-disabled and
warming pins stay green (the music gate is untouched for non-book items).

Known coverage boundary of the chosen anchor, recorded for shape (b): an
AUDIO-typed chapter under a PLAIN-FOLDER book with no AudioBook ancestor
(the metadata-remap shape) does not resolve as book-shaped through
IsAudioBookOrChapter and would still launch; the book-discriminator routing
shape (b) implements covers that class.

Gates: /simplify 4 findings applied + 2 skips recorded (see JF-804's summary;
the gates covered both tasks in one run). /code-review high: no correctness
bugs; F1 (deep-miss log), F2 (Sum prefix form), F3 (WarmTrackerAt doc) are
JF-804-surface; F4 (the ordering pin) applied here; F5 dispositioned as the
JF-803 census already updated same-turn. Suites: touched battery 160/160 both
TFMs at the final state; Release -warnaserror 0/0 both TFMs; full suite
5490/5490 both TFMs (baseline 5484 + 6).
<!-- SECTION:FINAL_SUMMARY:END -->
