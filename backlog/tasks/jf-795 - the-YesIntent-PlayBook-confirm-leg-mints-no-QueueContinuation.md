---
id: JF-795
title: >-
  JF-795 - the YesIntent PlayBook confirm leg mints no QueueContinuation (long
  confirm-queued books truncate at the initial fetch size)
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
labels:
  - bug
  - audiobooks
dependencies:
  - JF-793
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-793 Finding 1 round (2026-10-06), same-turn per the
review-recommendation rule (the in-task decision the JF-793 filing asked for: the
adoption does NOT cover it). The YesIntent PlayBook confirm leg
(`YesIntentHandler.PlayBook`) resolves the confirmed book's chapters with a SINGLE
page (`BuildScopedAudiobookChaptersQuery(... startIndex: 0, limit:
GetInitialFetchSize())`) and never mints a QueueContinuation, never sets the device
queue, and has no DeviceQueueManager dependency at all: a "yes" confirm on a book
longer than the initial fetch size (5) queues ONLY the first 5 chapters and the book
ends there, while the direct ask plays the whole book through the tail fetcher. This
violates the confirm-must-match-ask rule on the queue-completeness axis, a pre-existing
gap JF-791's head fix made visible (the head path now mints the continuation; the
confirm leg still does not).

Fix shape to design in-task: either mint the continuation + device queue in the
confirm leg (threading DeviceQueueManager into YesIntentHandler and replicating the
JF-673/JF-693/JF-674 minting invariants: StartIndex bookkeeping, unknown-total
regime, MintedQueueItemIds binding, refusal-before-state ordering), or route the
confirm through the head path's shared page-and-mint machinery so the two cannot
drift (the AlbumPlayService precedent). PREFER the routing branch (the JF-793
/simplify altitude round, 2026-10-06): the head's minting state grew again in JF-793
(the deep-resume continuation triplet: continuationStartIndex/TotalCount/HasMore
rebased at the position-holding chapter's page), so a replication branch would now
copy the whole page+resume+mint flow; routing comes with it for free.

The same round widened the task's scope by one axis: the confirm leg also never
resumes (it launches trackItems[0] with no FindResumeTrackIndex at all, and
YesIntentHandler holds no IUserDataManager), so an ask that resumes a book at its
position answers a confirm by restarting the book at chapter 1 at 0:00 - the same
confirm-must-match-ask divergence JF-793 Finding 4 just closed on the head's
deep-progress axis. The routing branch covers the resume axis for free; a
replication branch would have to thread the resume decision too. Red pin: confirm a
26-chapter book, expect the continuation minted with TotalCount 26 (pre-fix: null).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors: full-solution Debug build plus the Release -warnaserror build, 0 warnings and 0 errors, both TFMs, at the final post-gate state.
- [x] #2 dotnet test passes: full suite 5421/5421 on net10.0 and net9.0 at the final state (baseline 5418 + the two JF-795 pins + the gate pin from the /simplify round); touched+adjacent battery 229/229 both TFMs.
- [x] #3 No new compiler warnings introduced: the Release -warnaserror run is the proof (0 warnings).
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization: N/A, no session attributes touched (the confirm leg writes only the session queue/now-playing items it already wrote).
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress: N/A, no HttpClient surface touched.
- [x] #6 NLU test fixtures updated if interaction model changed: N/A, no interaction model, locale, or speech changes (the STOP condition for new strings never fired; the gate reuses the existing FeatureDisabled key present in all 17 locales).
- [x] #7 E2E test added for new intent or handler logic: N/A per the worker split (no deploy from this branch, the E2E suites need live SMAPI + the deployed build); the handler-logic coverage is the four YesIntent book-confirm pins (continuation mint, deep-progress resume, books-disabled gate, and the existing climb/folder-payload/single-file pins) plus the untouched head pins proving byte-identity of the ask.
- [x] #8 Locale response strings added to all 17 locales: N/A, no new user-facing strings (see #6).
- [x] #9 /simplify passed: 4 parallel angles (reuse/simplification/efficiency/altitude), 5 applied (the BooksEnabled gate on the confirm, the nullable spokenBookName post-climb fallback, the SetupConfirmedBook fixture hoist, the dead-mock and dead-using removals, the re-indent), 5 reasoned skips recorded in the simplify commit; two reviewer dead-using claims corrected of record (one was dead and removed, the other was live and restored, build-verified).
- [x] #10 /code-review high passed: 5 findings, all dispositioned. F2 APPLIED (the NativeControlsForBooks confirm test restores its flag in a finally); F5 FILED as JF-802 (the pre-existing BooksEnabled gap on the song-typed single-file-book confirm; the reviewer's hoist-above-dispatch fix rejected as wrong altitude, both candidate shapes recorded); F1/F3/F4 SKIPPED with reasons (ambient-Plugin statics moved verbatim with head-side coverage; the Mock.Of fixture debt already recorded in the simplify skip note; the leaf-confirm double lookup bounded to legacy payloads by the is-AudioBook short-circuit).
<!-- DOD:END -->

## Final Summary

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

The confirm-must-match-ask invariant is now structural, not mirrored. The task's
preferred ROUTING BRANCH was evaluated and TAKEN: the confirm context reaches the
head's composition cleanly through the PodcastEpisodeResolver precedent (JF-599/JF-605,
the exact shape YesIntentHandler already consumes for podcast confirms), so no
replication of the page+resume+mint flow exists anywhere. The new
Alexa/Util/AudiobookPlayResolver.PlayBookAsync owns, moved verbatim from
PlayBookIntentHandler's own HandleAsync (locals to parameters; RetryAsync and
SafeGetItemsResult translated to their ONE static cores, the same translation the
precedent made): the JF-791 chapter-leaf climb, the JF-670/JF-673 initial page with
the unknown-total regime, the JF-361 single-file fallback, the resume decision with
the JF-793 finding-4 deep-resume re-slice, the JF-693/JF-699 refusal-before-state
ordering, the JF-674 continuation mint with the device queue, and the
NativeControlsForBooks tracked/cold/fresh plus flat-AudioPlayer launch branches.
The head delegates after its search/disambiguation; YesIntentHandler's private
page-only PlayBook leg is DELETED and its routing gate calls the shared flow, gaining
IUserDataManager as the one ctor dependency the resume axis needed (DI resolves it by
type; the five test construction sites updated mechanically). The only per-caller
arguments are logLabel (triage identity) and spokenBookName (the no-content Tell's
argument; made nullable by the /simplify round so the confirm speaks the post-climb
name exactly as its deleted leg did).

RED PROOFS on the unmodified production tree, both TFMs, committed as the red-proof
commit before any production change: the continuation pin
(HandleAsync_DisambiguationAlbumType_BookFolderConfirm_MintsQueueContinuation)
failed `Assert.NotNull() Failure: Value is null` at the QueueContinuationStore.Get
assertion, confirming the leg minted nothing; the resume pin
(HandleAsync_DisambiguationAlbumType_BookConfirm_DeepProgress_ResumesAtPositionHoldingChapter)
failed with the chapter-1 token where the position-holding chapter 22 (of 26, at 10
minutes) was expected. Both green post-fix: the continuation mints with SourceType
Audiobook, ParentId the book folder, StartIndex 5, TotalCount 26; the deep progress
launches chapter 22 at its position with the queue re-sliced to chapters 22-26 and no
continuation. Companion pins held: the collapsed single-file book under the shared
container still plays as its own track, and the MusicAlbum confirm leg is untouched
(git diff shows only comment lines near it; the JF-767 pins green).

THE DISPATCH ENUMERATION (confirm == ask by construction, one code path): fresh
multi-chapter book; deep progress beyond the page; in-page progress;
after-last-played; single-file book as its own track with no continuation; collapsed
single-file book under a shared container (climb fails closed on both); no-audio
Tell; NativeControlsForBooks tracked/cold/fresh arms; refused launch leaving no state
writes; device-queue write. Ten shapes, one composition.

SUITES: touched classes 228/228 then 229/229 (with the gate pin) both TFMs;
intermediate executor-migration collateral of 8 pre-existing book-confirm pins
resolved by moving their mocks to the GetItemsResult page executor the confirm now
rides (assertions unchanged). Final state 5421/5421 both TFMs (baseline 5418 + 3
pins). Release -warnaserror 0/0. Gates: /simplify 4 angles (5 applied, 5 reasoned
skips, 2 reviewer claims corrected of record) + /code-review high (5 findings: 1
applied, 1 filed as JF-802, 3 skipped with reasons). Not deployed (worker branch
only). The efficiency reviewer verified the extraction introduces no regression on
the head path (retry wrapper, page executor, climb count, deep-resume trade, closure
lifetime all byte-equivalent or strictly better: one list allocation removed per
chapter-leaf play).

GATE-MARKER RECORD (2026-10-06): the extraction verified verbatim on the direct path (line-for-line, only locals-to-parameters renames); the ten-shape confirm==ask claim holds structurally; DI satisfied by the existing IUserDataManager registration plus auto-discovery. Findings: F1 (the deep-resume fetch on every first-ever ask, now doubled to the confirm) tracked via the JF-797 addendum; F2 (the payload-kind breadth interacting with the new gate) tracked via the same addendum; F3 APPLIED (the finally restores the CAPTURED pre-test flag value, not the literal false; the sweep found no other literal-restore siblings of this shape); F4 (the Mock.Of<IUserDataManager> fixture debt in ResumeOnRelaunch/ShowMore suites) left as the recorded JF-465-class skip, now noted as load-bearing; F5 APPLIED (the redundant null-forgiving dropped); F6 REFUTED with reason - the BooksEnabled gate cannot hoist above the item fetch because the payload TYPE (which requires the fetch) is what distinguishes a book confirm from an album confirm under the shared MediaTypeAlbum label; hoisting would answer FeatureDisabled for album confirms with books off, and the path is a rare stale-prompt interaction, not the hot ask path.
<!-- SECTION:NOTES:END -->
