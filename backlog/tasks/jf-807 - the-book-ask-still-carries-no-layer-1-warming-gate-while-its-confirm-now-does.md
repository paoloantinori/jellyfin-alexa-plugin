---
id: JF-807
title: >-
  JF-807 - the book ask still carries no Layer-1 warming gate while its confirm now does
status: Done
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-806
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-806 round (2026-10-07, same-turn per the
review-recommendation rule; the /simplify altitude review surfaced it).

JF-806 folded the warming axis onto the YesIntent confirm legs per the
JF-805 gate marker, including the BOOK confirm leg (the marker's explicit
instruction, protective on the JF-795 composition's widened surface: the
paged chapter fetch plus per-chapter UserData reads). But the book ASK,
PlayBookIntentHandler, still carries NO Layer-1 warming gate: it is absent
from WarmingGateCoverageTests.ExpectedGatedHandlers and makes no
GuardIndexReady call, so during the post-restart index-load window the ask
runs the same cold-database composition the confirm now refuses. The
confirm is more protected than the ask: the residual asymmetry the marker's
fold introduced deliberately.

FIX SHAPE: the PlayAlbumIntentHandler precedent is the pattern: the coarse
GuardIndexReady(_artistIndex) entry gate with the "album paths have no
in-memory index of their own to gate on, so the artist index stands in for
the shared cold database" rationale (PlayBookIntentHandler would need the
IArtistIndex ctor param threaded). Add the ask to
WarmingGateCoverageTests.ExpectedGatedHandlers when gated, and decide
whether the JF-806 book-confirm gate's ordering comment (which today
documents the ask's ungated state) needs its premise updated in the same
change. Optional companion: a SkillWarmingUpTests-style reachability pin
for the ask entry.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release build both TFMs: 0 errors, 0 warnings)
- [x] #2 dotnet test passes (full suite 5457/5457 net9.0; net10.0 5457/5457 after ONE unreproducible flake in the unrelated ReminderLocaleStringsTests, filed as JF-809 with the reproduction matrix: class alone 30/30, two subsequent full runs green, base dca7e165 green)
- [x] #3 No new compiler warnings introduced (Release -warnaserror ruleset build: 0 warnings)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched; the warming refusal is a session-ending Tell that must carry none, the JF-387 rule the JF-806 F5 tradeoff documents)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model or utterance changes)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; the gate is a refusal window only reachable with a cold index, which a live warm server cannot present to simulate-skill; covered by the four unit pins plus the WarmingGateCoverageTests IL roster scan, the suite's own enforcement shape for this layer)
- [x] #8 Locale response strings added to all 17 locales (N/A: the SkillWarmingUp string already exists; no speech changes)
- [x] #9 /simplify passed (4 review agents: 3 findings applied, covering the stand-in rule hoisted to IndexWarmingGate's class doc with the per-handler comment shrunk to a pointer, SetupSingleBookPlay made the single owner of the single-book mock shape, and the readiness-mock pair deduplicated; efficiency angle returned no findings; 0 skips)
- [x] #10 /code-review high passed (3 findings applied: the warming pin now enforces the no-progressive-announcement placement contract via Progressive.AllText, the WarmingArtistIndex/ReadyArtistIndex pair hoisted to TestHelpers as the one owner with YesIntentHandlerTests rewired, the dead book mocks dropped from the intersection pin; 0 outstanding)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINALSUMMARY:BEGIN -->
The inverted asymmetry is closed: the book ASK (PlayBookIntentHandler) now
carries the same Layer-1 warming gate its JF-806 confirm leg carries.

INDEX-CHOICE DECISION (verified from source): the book ask's cold surface is
the recursive AudioBook SearchTerm GetItemList scan, the SearchItemsFuzzyAsync
cascade, the NormalizeBookCandidates folder climbs, and the resolved-book
composition (AudiobookPlayResolver.PlayBookAsync's paged chapter fetch plus
per-chapter UserData reads); NONE of it reads the artist or song n-gram
index, and no dedicated book index exists. That is the PlayAlbum shape, not
the PlaySong shape (PlaySong gates the song index because its fast path IS
that index; gating an index the path never reads would be dishonest). So the
artist index stands in for the shared cold database, per the PlayAlbum
precedent, and it must be the ARTIST index specifically because the
confirm-must-match-ask rule extends to the warming answer: the JF-806 confirm
gates the artist index, and a song-index stand-in would make ask and confirm
diverge in the window where the two indexes' readiness differs (the song
index's cold window outlasts the artist's). The shared rule now lives once on
IndexWarmingGate's class doc (the /simplify altitude round moved it there).

PLACEMENT: after the BooksEnabled gate and the empty-slot ElicitBookName Ask,
immediately before the SearchingBook progressive announcement. Books-then-
warming mirrors the confirm's own order (the warming+disabled intersection
answers FeatureDisabled on both sides); elicit-then-gate follows the
QueryArtistLibrary/AddToQueue majority shape (PlaySong/PlayAlbum's
warming-before-elicit order is their Dialog.ElicitSlot-specific exception);
before-the-announcement is the CLAUDE.md Layer-1 contract, now ENFORCED by a
pin (code-review F1: Progressive.AllText must be empty on the refusal).

RED PROOF (pre-gate, ctor scaffolding disclosed, both TFMs):
HandleAsync_BookAsk_WhileIndexWarming_ThrowsAtEntry failed with the clean
no-throw ("Assert.Throws() Failure: No exception was thrown; Expected:
typeof(SkillWarmingUpException)") - the ungated query ran and played, served
by the single-book mocks so no mock-default crash masked the shape. Post-fix
the pin passes and the roster scan (PlayBook added to
WarmingGateCoverageTests.ExpectedGatedHandlers) is green both directions.

Companion pins: ReadyIndex_PlaysUnchanged (the warm path never converts);
WarmingAndBooksDisabled_AnswersDisabledFirst (the intersection order lock);
WhileIndexWarming_EmptySlot_StillElicitsBookName (the elicit hatch survives
the warming window).

Comment premises updated in the same change (the task's open question,
answered yes): the JF-806 book-confirm gate comment in YesIntentHandler and
the two YesIntentHandlerTests doc comments no longer claim the ask is ungated.

VERIFICATION: touched classes 89/89 both TFMs at each round; full suite
5457/5457 net9.0 and net10.0 (net10.0 after one unreproducible flake in the
unrelated ReminderLocaleStringsTests, filed as JF-809); Release -warnaserror
build 0 warnings 0 errors both TFMs.

GATES: /simplify (3 applied, 0 skips) and /code-review high (3 applied,
0 outstanding) both ran as literal Skill calls with dispatched reviewers.
<!-- SECTION:FINALSUMMARY:END -->
