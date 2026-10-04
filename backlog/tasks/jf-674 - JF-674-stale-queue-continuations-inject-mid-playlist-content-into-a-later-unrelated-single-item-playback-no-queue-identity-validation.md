---
id: JF-674
title: >-
  JF-674 - stale queue continuations inject mid-playlist content into a later
  unrelated single-item playback (no queue-identity validation)
status: To Do
assignee: []
created_date: '2026-09-29 15:02'
labels:
  - playback
  - progressive-queue
  - tech-debt
dependencies: []
references:
  - >-
    backlog/tasks/jf-670 -
    JF-670-the-Audiobook-progressive-continuation-is-a-dead-letter-no-fetcher-case-books-truncate-at-the-initial-page-and-PostPlay-AutoPlay-can-append-music-radio-after-a-book.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-29 same-turn (the promise made when the JF-670 F1 finding came back: "we will file the queue-lifecycle task"). Filed by the orchestrator; evidence gathered by the JF-670 worker.

THE FINDING (JF-670 gate-review F1, medium): a stale QueueContinuation outlives the playback it was minted for and injects its content into a LATER, unrelated single-item playback. Concrete new symptom: play a long audiobook, stop mid-book (the store entry is NOT removed on PlaybackStopped), then play ONE song; at that song's PlaybackNearlyFinished (remaining 0 <= prefetch threshold) TryFetchContinuationBatch fetches the stale Audiobook continuation and appends mid-book chapters after the song. Pre-JF-670 the Audiobook arm was a dead letter that silently self-cleaned, so this cross-playback injection is NEW for books; the music arms (Album/Artist/Playlist) have always had the same lingering-store property, just with same-media-type content.

WHY NO SMALL FIX EXISTS (worker-verified evidence, do not re-litigate without new facts):
1. DeviceQueueManager.SetQueue is called by only the five multi-item play paths (PlayArtistSongs, AlbumPlayService album + playlist, CrossMediaFallback, FollowMe, PlayBook). Single-item plays never call it (ProgressReporter.cs:584 states "a fresh single-song play, which never calls SetQueue").
2. session.NowPlayingQueue is assigned ad hoc at 39 sites across 25 files; no shared funnel.
3. BuildAudioPlayerResponse(PlayBehavior.ReplaceAll) IS the one call every play funnels through (25+ sites), but ReplaceAll does NOT mean "new logical queue": LaunchRequestHandler/ResumeIntentHandler/StartOver/JumpToPosition/SkipForwardBack/ProgressReporter resume-confirm all ReplaceAll the SAME logical queue, and the store entry legitimately keeps serving that queue across a stop (documented linger semantics on QueueContinuation.CachedTracks; the JF-574 resume flow depends on it).
4. All five continuation creators Set their entry BEFORE the launch call, so a Remove inside the response builder would erase the fresh continuation it is launching, and reordering the five Sets after launch breaks long-queue resume.

THE WORK: introduce a queue-identity concept so a continuation is bound to the queue it was minted for and validated at fetch time (fetch only when the current queue still matches the minted identity; mismatch = discard entry), OR an explicit creator-side invalidation lifecycle (every NEW logical queue mint invalidates prior entries, while same-queue ReplaceAll keeps them). Both need a definition of "new logical queue" that survives the resume paths above. This is decision + design work first: the identity key candidates (queue anchor item? mint-time queue fingerprint? explicit generation token on the session?) each have failure modes to write down before choosing.

VERIFICATION: a red-green pin for the injection scenario (stale book continuation + fresh single-song play => no fetch at exhaustion); the music linger semantics stay working (stop album mid-way, resume same album => continuation still serves, JF-574 pin); full suite both TFMs.

OUT OF SCOPE: any change to the per-arm fetchers themselves (JF-666/JF-670 shapes stay); the JF-672 chapter-order and JF-673 fallback-total probes ride Paolo's device round separately.
<!-- SECTION:DESCRIPTION:END -->

## Design Decision (2026-10-04, written before implementation)

**CHOSEN: fetch-time queue-identity validation (the description's candidate A).** Each
continuation records, as its identity, the item-id SET of the queue page it was minted
over (`QueueContinuation.MintedQueueItemIds`, captured at the five mint sites from the
exact `queueItems` list installed into `session.NowPlayingQueue`). At fetch time
(`TryFetchContinuationBatch`, placed AFTER the current-index guard and BEFORE the
threshold guard) the entry serves only when EVERY minted id is still a member of the
live session queue (`SessionQueue.IdSet` is the membership source); a mismatch removes
the store entry and skips the fetch. NO creator-side invalidation hook exists anywhere:
a creator that does not mint (small source) simply leaves the old entry for the fetch-time
validation to discard.

### Identity-key candidates weighed (failure modes)

1. **Single queue anchor item** (the minted queue's first or last id).
   - FIRST-item anchor: PlayNext's AfterCurrent insert legally lands at the FRONT when
     the current item is not in the device queue (`DeviceQueueManager.ResolveInsertIndex`
     falls back to index 0), so a legitimate "play X next" changes the queue head and
     would discard a live album/playlist continuation.
   - LAST-item anchor: survives appends and mid-inserts, but leaves the boundary hole:
     a single-song play OF exactly the anchor item validates (a one-item queue contains
     the anchor) and serves the stale continuation after the song; and single-anchor
     coincidence (the anchor id appearing inside an unrelated later queue, e.g. a
     YesIntent confirm of a different source sharing a compilation track) cross-serves
     stale content.
   - Strengthening either means carrying more ids, which IS the chosen set shape.
2. **Mint-time queue fingerprint** (ordered hash of the whole queue).
   - The fetch itself mutates the queue (`SessionQueue.AppendUnseen`), so the
     fingerprint must be rewritten after every fetch; Amazon multi-fires
     NearlyFinished concurrently and a last-write-wins rewrite can record a
     fingerprint omitting a sibling batch's contribution, so the NEXT fetch discards a
     LIVE continuation mid-playlist.
   - AddToQueue/PlayNext/ShuffleOn/ShuffleOff/RestoreOrder mutate the queue without
     touching the store: every queue edit would discard a live continuation (the album
     tail dies after one "add this song too").
   - The JF-574 rehydration mirror rebuilds the session queue from the device store,
     which holds only the minted slice while the live session may have held
     slice+batch: mismatch discards a live continuation right after a session
     re-registration, breaking the exact linger semantics this task must preserve.
3. **Explicit generation token on the session**.
   - No shared funnel for "new logical queue": the five creators could bump, but the
     actual bug vector (single-item plays) assigns `NowPlayingQueue` ad hoc at ~two
     dozen sites (grep 2026-10-04); each needs the bump and a missed one silently
     resurrects the injection.
   - The one shared funnel, `BuildAudioPlayerResponse(ReplaceAll)`, is shared with the
     resume paths (LaunchRequestHandler.HandleSessionQueueResume, ResumeIntentHandler
     x2 tails, StartOverIntentHandler.BuildRestartLaunchAsync, JumpToPositionIntentHandler,
     SkipForwardBackIntentHandler, ProgressReporter.ServeAdjacentQueueItem,
     YesIntentHandler.HandleResumeConfirmation), which ReplaceAll the SAME logical
     queue; a builder-side bump discards the continuation on the FIRST resume - the
     JF-574 linger semantics die. (Note: the filing's evidence point 4 premised
     creators-Set-BEFORE-launch; JF-693/JF-699, landed 2026-10-02 AFTER this task was
     filed, moved all five Sets AFTER the launch build, so that specific ordering
     premise is stale on the current tree. The resume-path collision alone still kills
     the builder-side shape, so the no-small-fix conclusion stands.)
   - Session-scoped storage dies on the restart/re-registration shapes where the queue
     itself survives in the device store: a fresh session reads a fresh generation
     while the rehydrated queue IS the old logical queue.
4. **CHOSEN - minted-page id SET membership, validated at fetch time**.
   - Same-queue mutations all preserve the superset relation: the fetch's own appends;
     AddToQueue/PlayNext inserts at any position (inserts never remove);
     ShuffleOn/ShuffleOff/RestoreOrder mirrors (`MirrorQueueToSession` never shrinks
     the playable set); the JF-574 rehydration (the device store holds exactly the
     minted slice at mint time and only grows by `Enqueue`); and every resume-path
     ReplaceAll relaunch (verified 2026-10-04: none of the seven sites above assigns
     `session.NowPlayingQueue`).
   - Different-queue takeovers all break membership: single-item plays (a one-item
     queue cannot contain a multi-item page); fresh multi-item plays of another source
     (which re-mint, or leave the stale entry to be discarded); YesIntent confirm-leg
     rebuilds of a different source; ClearQueue's trim-to-current. ClearQueue is a
     deliberate behavior improvement: today the "cleared" queue regrows the album tail
     at the next NearlyFinished (the continuation lingers; ClearQueue already
     invalidates the precompute cache for the same reason, JF-424.1) - after this
     change the fetch-time validation discards the entry with no new ClearQueue hook.
   - Residual hole (accepted): a minted page of size 1 (InitialFetchSize is
     user-configurable down to 1; default 5) played as a single song still validates.
     Needs a deliberate non-default config AND playing exactly the one paged item.
   - Ordering constraint: NONE (no creator-side hook at all).
   - Cost: O(page + queue) set membership, paid only when a continuation exists AND
     the current item is located (page <= 20, queue <= hundreds).
   - Back-compat: an entry minted WITHOUT ids (hand-constructed test shapes; no
     production site after this change) skips validation. Production wiring is
     enforced structurally by an IlCallScanner roster test (the JF-720 writer-roster
     idiom): every `new QueueContinuation` construction site in the plugin assembly
     must initialize `MintedQueueItemIds`.
   - Placement: validation runs between the current-index guard and the threshold
     guard, so the discard fires exactly where a fetch would actually have been
     considered. (Guard ORDER is behaviorally equivalent for the entry's fate: on
     the unlocatable-current-item shape the fetch skips and the exhaustion Remove
     drops the entry regardless, the JF-712-documented class; order affects only
     which guard logs.)

### Addendum-2 sub-case (do-not-mint-on-VideoApp): DECLINED

The identity validation closes the injection harm generically (the later single-song
play's queue cannot contain the book's page, so the fetch discards the entry). And a
VideoApp-minted continuation has a legitimate serve path:
`ResumeIntentHandler.TryBuildNativeControlsBookResumeAsync` returns null on the
chapter-progress/cold-tracker shape (trackedTicks <= 0 && fallbackTicks > 0, and for a
chapter-shaped item at the first guard: `IsAudioBook(item)` is `item is AudioBook`,
false for a chapter leaf), after which the tail flat-resumes the CHAPTER via
AudioPlayer with the session queue still the book page - there the continuation is the
only thing that carries the book past its initial page, so suppressing the VideoApp
mint would reintroduce the JF-670 dead-letter truncation on that flow. (The tracker
persists to disk, so the cold-tracker shape is narrow but real.) The mint stays on all
three PlayBook arms.

### JF-673 addendum (coordinator context, 2026-10-04)

JF-673 (merged to main after this branch cut) makes END-UNKNOWN audiobook
continuations exist and persist for the first time on NRE-class servers
(TotalCount carries the SearchService.UnknownTotal sentinel, int.MaxValue;
pre-JF-673 no audiobook continuation was ever stored there). Consequence for
this design: the stale-continuation injection surface now includes BOOKS on
those servers. Nothing changes in the identity mechanism: validation never
reads TotalCount (the sentinel only governs exhaustion arithmetic), so an
end-unknown lingering book entry is a first-class stale candidate handled by
the same page-membership rule. The injection pin runs as a Theory over both
totals (finite 20 and the int.MaxValue sentinel; the SearchService.UnknownTotal
constant is referenced by name in the pin's doc and swapped in when the JF-673
merge reaches this tree). JF-753 (the album path's engagement gap) is
separately tracked; this change's AlbumPlayService edit is the mint initializer
only and deliberately does not touch it.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DECISION: fetch-time queue-identity validation. `QueueContinuation` gained
`MintedQueueItemIds` (the item-id SET of the queue page the mint installs into
`session.NowPlayingQueue`, captured through the ONE `QueueIdsOf` projection at
all five mint sites: PlayArtistSongs, CrossMediaFallback, AlbumPlayService
album + playlist, PlayBook's ApplyBookPlaybackState) and
`IsForLiveQueue(session)` (membership via `SessionQueue.IdSet`). The ONE
validation site is `TryFetchContinuationBatch`, between the current-index guard
and the threshold guard: a live queue missing any minted id discards the store
entry (Information log, no causal claim) and skips the fetch. NO creator-side
invalidation hook anywhere; NO ordering constraint.

FAILURE-MODE TABLE (full reasoning in the Design Decision section): single
anchor item (first: broken by PlayNext front-inserts; last: boundary hole where
a single-song play OF the anchor serves the stale tail), full queue fingerprint
(breaks on the fetch's own appends, queue edits, and the JF-574 rehydration
rebuild), session generation token (no funnel for single-item plays; the one
shared funnel, BuildAudioPlayerResponse(ReplaceAll), is shared with the resume
paths so the first resume kills the JF-574 linger). Chosen set-membership:
same-queue mutations all preserve the superset (fetch appends, inserts at any
position, shuffle mirrors, rehydration, resume relaunches), different-queue
takeovers all break it (single-item plays, fresh non-minting plays, confirm
rebuilds, ClearQueue's trim). Residual hole: a size-1 page (InitialFetchSize
configurable to 1) played as a single song; needs non-default config.

ADDENDUM-2 SUB-CASE (do-not-mint-on-VideoApp): DECLINED with evidence; see the
decision section (the cold-tracker chapter resume serves a VideoApp-minted entry
legitimately; the identity validation already closes the injection harm).

PIN DISPOSITIONS: the injection pin (book mint via the real PlayBook handler,
single-song swap, NearlyFinished asserts queue-single + store-null) RED on the
unmodified base on BOTH TFMs (queue held 6 items: the song plus 5 mid-book
chapters), green after; runs as a Theory over finite and end-unknown totals
(the JF-673 coordinator context). The music-arm twin pin (album continuation +
single song) red under the disabled-guard sabotage. The linger pin (album mint
via the real PlayAlbum handler with a DeviceQueueManager, JF-574 session-wipe +
rehydration, then TWO fetch legs: rehydrated queue serves 15, extended tail
serves to 20 and exhausts) red under an exact-equality predicate sabotage
(set-membership is load-bearing). Three predicate unit pins (empty identity,
superset/order-free, missing id). Two structural roster facts (construction
sites must wire the identity: red proven by deleting one initializer, the
roster named BuildArtistSongsResponseAsync; the fetch guard must call
IsForLiveQueue) plus the resume-path setter roster (the five ReplaceAll-relaunch
handlers never assign NowPlayingQueue; guards the flat-resume serve path the
declined sub-case rests on).

GATES: /simplify (4 angles) applied 2 (three byte-identical mint comments
collapsed to a one-line pointer + the guard comment trimmed to site-local
rationale; roster hand-composed handler scan replaced with
HandlerChainMethods), skipped 1 with reason (the IsForLiveQueue `.All` rewrite:
taste-level, the explicit loop keeps the documented empty short-circuit
visible); efficiency CLEAN (HashSet per fetch judged acceptable: gated behind
the index guard, one O(n) pass, no I/O). /code-review high (two passes, 8
angles): applied 4 (the discard log no longer asserts an unobservable cause
"later playback replaced the queue" and now carries deviceId; the empty-capture
doc states the empty-page-slice shape honestly; the roster vacuity guard
relaxed from a hardcoded 5 to > 0 with a message distinguishing consolidation
from scan breakage; the resume-path unpinned invariant got its structural
roster fact), FILED 1 as JF-750 (the pre-existing PlayArtistSongs
artistsItems[0] wrong-item launch, verified in source and confirmed by both
passes), REFUTED 1 (the claimed PlayBook startIndex==Count crash:
ResumeMath.cs:397 guards `lastPlayedIndex + 1 < tracks.Count`, the
fully-played-page shape returns (0,0); refutation recorded inside JF-750 so the
next round does not re-derive it).

COUNTS: 5136/5136 both TFMs on the final state (merged-tree baseline 5126 +
10: the 7-case ProgressiveQueueTests additions + 3 roster facts); every
filtered round green; Release --no-restore -warnaserror 0 warnings 0 errors.
Per-arm fetchers untouched (JF-666/JF-670 shapes unchanged). No locale, model,
or speech surface changed; no deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: 0 warnings 0 errors on the final state; Debug builds clean throughout)
- [x] #2 dotnet test passes (5136/5136 net9.0 AND net10.0 on the final state)
- [x] #3 No new compiler warnings introduced (Release -warnaserror 0 warnings)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (no session-attribute or serialized shape change: the store is in-memory only, the new property is IReadOnlyList<Guid> on a non-persisted type)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction-model change)
- [x] #7 E2E test added for new intent or handler logic (N/A for the SMAPI e2e suite: no new intent/handler surface; the behavioral pins drive the REAL PlayBook/PlayAlbum mints end-to-end through the real PlaybackNearlyFinished fetch, which is this change's e2e)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing string changed; the one new log line is operational, not spoken)
- [x] #9 /simplify passed (4 angles; 2 applied, 1 skipped with reason, efficiency CLEAN)
- [x] #10 /code-review high passed (two passes, 8 angles; 4 applied, 1 FILED as JF-750, 1 refuted with evidence, 0 open)
<!-- DOD:END -->

ORCHESTRATOR ADDENDUM 2 (2026-09-29, verified in source post-merge): the store site (PlayBookIntentHandler ~:249-262) sits BEFORE the NativeControlsForBooks split, so a VideoApp book play (the current production setting on the household box) mints an Audiobook continuation that no AudioPlayer event can ever serve - a dead entry by construction under this flag. The injection scenario is therefore reachable TODAY on the production config: VideoApp book (entry minted) -> later single-song AudioPlayer play on the same device -> song's exhaustion fetches mid-book chapters. The fix candidates in the description should weigh the cheap sub-case (do not mint a continuation on the VideoApp path, or clear it when the launch is VideoApp) alongside the full queue-identity design.
