---
id: JF-713
title: >-
  JF-713 - PlayPlaylist shuffle branch commits SetShuffledQueue before the launch
  build (derive-then-commit via shuffle snapshot)
status: To Do
assignee: []
created_date: '2026-10-02 15:05'
labels:
  - playback
  - queue
  - refusal-contract
dependencies:
  - JF-699
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-699 merge
(commit 65821618, finding 5 of 5). The PlayPlaylist shuffle branch (AlbumPlayService.cs
~980-1035) deliberately keeps SetShuffledQueue BEFORE the launch build - the in-code
comment names the reason (deriving the shuffled order before the build and shuffling
again at commit time would re-shuffle and disagree with the stored order) - so it is
the one site of the JF-699 reordered class where a refused launch still leaves written
state behind. Failure scenario: shuffled playlist start in seek mode with an empty
secret: SetShuffledQueue lands (the device queue is replaced with the shuffled order),
the builder then throws StreamTokenNotConfigured, and the device keeps a queue whose
contents never played while the session and continuation stay clean; a subsequent
resume or queue-browse answers the phantom shuffled queue. The playlist reorder was
otherwise documented as unpinnable end-to-end in JF-699 (GetManageableItems
non-virtual).

THE WORK: derive-then-commit via snapshot - compute the shuffled order ONCE into a
local, build the launch from it, and commit SetShuffledQueue with the SAME snapshot
after a successful build (the comment's re-shuffle objection is answered by reusing
the snapshot, not by re-deriving). Pin the refused-shuffle shape if the harness allows
(the JF-699 note says the playlist path is hard to pin end-to-end; a unit-level pin on
the service method with a throwing builder seam is acceptable evidence).

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit 9fe2c4fd, 3 low findings;
all five scrutiny axes verified mechanically: the snapshot agreement holds with nothing
between derive and commit receiving the snapshot, the public-method deletion left zero
live references with the JF-305 contract fully ported, ReplaceQueue is byte-identical
to the two inlined tails so the surviving-stores set carries exactly, the race window
matches the JF-699/JF-712 accepted class with no stale-payload persist possible, and
JF-723's inertness-at-today's-values premise confirmed): F1 APPLIED (SetQueue now
stores a defensive copy, giving both ReplaceQueue callers the same stored-queue-owns-
its-lists ownership contract CommitShuffledQueue documented; this also un-blinds the
shuffle refusal pin, whose seeded sentinel shared by reference could not distinguish
untouched from mutated-in-place); F2 APPLIED (the race note names ReplaceQueue directly
- the deferral target the same change completed - and names queue-editing intents
alongside playback events in the fire set); F3 APPLIED (the convention-only
snapshot/launch agreement documented on PendingShuffledQueue with the structural-guard
instruction for a second caller). Affected classes 105/105 both TFMs after the tail;
independent suite 4983/4983 both TFMs on the worker commit; merged-tree follows.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (full solution build succeeded on both TFMs after the review round; 0 errors)
- [x] #2 dotnet test passes (final full suite `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`: 4983/4983 net9.0 AND net10.0; baseline 4978 + 5 new tests)
- [x] #3 No new compiler warnings introduced (only the pre-existing xUnit1030 pair in the untouched VideoAudioControllerTests.cs; zero warnings from the changed files)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A: no session-attribute shape touched)
- [x] #5 HttpClient instances not shared across calls modifying BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E added for new handler logic (N/A per the task text: the playlist path is hard to pin end-to-end because Playlist.GetManageableItems is non-virtual and DB-coupled; the sanctioned unit-level pins on the service method landed in AlbumPlayServicePlaylistShuffleTests, driving BuildPlaylistPlayResponseAsync through a real resolvable Playlist (LinkedChildren + stubbed static BaseItem.LibraryManager) with the REAL config-driven builder refusal, red proof on both TFMs)
- [x] #8 Locale strings in all 17 locales (N/A: no user-facing strings; the reorder changes no response shape)
- [x] #9 /simplify passed (4 agents: reuse/simplification/efficiency/altitude; 5 findings applied incl. deleting the orphaned SetShuffledQueue wrapper, 4 skipped with reasons, efficiency clean; dispositions in the Final Summary)
- [x] #10 /code-review high passed (5 findings: 4 applied in-code incl. the commit-side defensive copies, the shared ReplaceQueue tail, the IReadOnlyList snapshot surface, and the KNOWN RACE note; 1 filed as JF-723 with its pointer comment; dispositions in the Final Summary)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Worker closure of JF-713. THE SNAPSHOT SHAPE: DeviceQueueManager.SetShuffledQueue was split into DeriveShuffledQueue (pure: copies the original order, Fisher-Yates the shuffled order, writes nothing) and CommitShuffledQueue (stores the snapshot verbatim with defensive copies, carries surviving stores, returns the stored queue); the snapshot is the new public PendingShuffledQueue record (IReadOnlyList surface, FirstItemId structural guard in the JF-712 PendingContinuation idiom). The orphaned SetShuffledQueue wrapper was DELETED (zero production callers after the reorder; its JF-305 observable pins were ported to DeriveAndCommit_* tests, and the stale Shuffler/FisherYates doc pointers updated). AlbumPlayService.BuildPlaylistPlayResponseAsync's shuffle arm now derives the snapshot, picks firstItem from pendingShuffle.FirstItemId, builds the launch, and only after a successful build commits the SAME snapshot (never a re-derive, answering the old in-code re-shuffle objection) and mirrors it into the session queue; the firstItem==null early return also no longer leaks a written queue. PIN STRATEGY: the service-level pins drive the service method directly (the sanctioned evidence shape) through a newly found fixture route: a real Playlist with LinkedChildren resolves via the stubbed static BaseItem.LibraryManager on both TFMs (net9.0 through GetLinkedChild/GetItemById, net10.0 through the batched ItemIds GetItemList), bypassing the non-virtual GetManageableItems limitation documented since JF-305; the refusal is the REAL one (seek mode + empty secret throws StreamTokenNotConfiguredException from the builder). Pins: refusal leaves a pre-seeded sentinel queue untouched (RED on both TFMs under the pre-JF-713 order, verified by temporarily restoring the old order), success commits the exact order the directive launched from (snapshot agreement: url/FullNowPlayingItem vs queue.ItemIds[0], original order preserved, session mirror with PlaylistItemId metadata, builder ledger carried), plus DeviceQueueManager split pins (seeded-Fisher-Yates oracle shared via helper, nothing stored on derive, verbatim never-reshuffle double commit, no cross-device aliasing after an Enqueue, empty-list contract throw). GATES: /simplify 4 agents (5 applied, 4 skipped-with-reason: the SetQueue-tail dedupe later overturned by the review round, the SeekModeBrokenConfig/CreateService hoists below threshold or out-of-diff, test-doc invariant mentions each naming its assert); /code-review high 5 findings, 4 applied (defensive copies + aliasing pin, ReplaceQueue shared store tail, IReadOnlyList surface, KNOWN RACE note citing the JF-712 precedent and the JF-699 twin) and 1 filed as JF-723 (TrimLaunchBaseIfNeeded judging the fresh launch-scope entry against the OLD queue's membership during the derive-to-commit window; inert today at base=0) with its in-code pointer. Hoisted the third StubBaseItemStatics copy into TestHelpers (the file's own hoist-on-third convention) and consolidated the two existing per-file twins. Suites 4983/4983 on both TFMs after the review round.
<!-- SECTION:FINAL_SUMMARY:END -->
