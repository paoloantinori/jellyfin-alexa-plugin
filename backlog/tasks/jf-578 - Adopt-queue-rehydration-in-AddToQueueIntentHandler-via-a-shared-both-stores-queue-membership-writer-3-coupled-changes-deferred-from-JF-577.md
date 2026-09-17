---
id: JF-578
title: >-
  Adopt queue rehydration in AddToQueueIntentHandler via a shared both-stores
  queue-membership writer (3 coupled changes, deferred from JF-577)
status: In Progress
assignee: []
created_date: '2026-09-16 13:49'
updated_date: '2026-09-17 11:39'
labels:
  - reliability
  - queue
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-577 adoption pass (2026-09-16): AddToQueueIntentHandler was deliberately NOT adopted for the ProgressReporter.TryRehydrateSessionQueueFromDevice guard. Evidence: (1) the handler writes ONLY the session queue (it holds no DeviceQueueManager), so after rehydration an add would land in the session store but not the device store it was rehydrated from, losing it on the next restart; (2) landing it in the device store too would make this handler a new queue-membership writer (today only play paths call SetQueue), the double-write coherence risk; (3) its FullNowPlayingItem == null branch would still ReplaceAll over the live stream on the rehydrated shape. A correct adoption needs three coupled changes: rehydrate at entry, route the add through a shared membership writer that updates BOTH stores, and re-scope the null-current branch. Scope when picked up: introduce the shared queue-membership writer (or extend DeviceQueueManager API), then adopt AddToQueue on top, with red-first tests for the restart-wiped shape and the both-stores persistence. Related: JF-574 (the fix), JF-577 (the hoist).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Session 2026-09-17 (implementation; status left In Progress, tree left uncommitted for the orchestrator's gates).

## Writer shape (one API covering both intent shapes)

- `DeviceQueueManager.Enqueue(deviceId, itemId, placement, currentItemId)` plus the nested `DeviceQueueManager.QueueInsertPlacement { End, AfterCurrent }`: the durable DEVICE-store leg only. ItemIds insertion (End appends; AfterCurrent inserts behind the caller-resolved current item, front fallback), CurrentIndex bookkeeping (the pointer advances by one exactly when the insertion point is at or before it, so it keeps naming the same physical item; the CurrentItemId/CurrentPositionTicks playback-position pointers are untouched, they belong to the event writers), OriginalItemIds shuffle-snapshot append so RestoreOrder keeps the user's added item (restored position is the end: the original-order position of a "next" ask under shuffle is undefined, its membership is not), and a missing/empty queue is seeded with the single item at CurrentIndex=-1. Coherence contract on the API doc.
- `ProgressReporter.EnqueueToBothStores(...)` (internal static, beside the mirror it composes with): device leg + session leg. The session leg MIRRORS from the mutated device queue (`MirrorQueueToSession`; device order authoritative per JF-447) exactly when both stores were populated before the add, so the two orders cannot drift across the write; in the degenerate shapes (no populated device store to be an order authority, or an EMPTY session queue the guard already declined to rehydrate) it keeps the pre-JF-578 direct session mutation verbatim (End appends; AfterCurrent behind current or front, the former PlayNext InsertAfterCurrent). A null manager degrades to session-only, today's behavior, which keeps every pre-existing handler test call site byte-identical.
- `ProgressReporter.RehydrateAndEnqueueToBothStores(...)`: the handlers' ONE entry point, coupling guard + `ResolveCurrentItemId` + write (the JF-582 unsplittable-pair lesson); returns the resolved current item id the callers branch on. Session-leg home rationale: the JF-574 layering decision (the Playback store must not gain a Handler-layer reference for the mirror; DeviceQueueManager owns only the durable mutation), documented on both APIs.

## Adoptions (both adopted)

- AddToQueueIntentHandler: ADOPTED. Ctor gained the optional `DeviceQueueManager?` param (the ListQueue precedent, DI-injected singleton; existing test call sites unaffected). The tail mutation routes through the writer with End placement; the `FullNowPlayingItem == null` branch is re-scoped to `currentItemId == null` (ResolveCurrentItemId: now-playing item first, coherent token stand-in on the rehydrated/second-adopter shapes), so the ReplaceAll-over-live-stream launch now fires only when genuinely nothing plays.
- PlayNextIntentHandler: ADOPTED, same writer, AfterCurrent placement. NO material shape difference (decided with evidence): its session-side insert (after current, front fallback) maps one-to-one onto the writer's placements; the JF-424.1 precompute invalidation stays in the handler (the insertion still displaces the successor); the private InsertAfterCurrent was deleted, its logic lives in the writer's session leg.

## Roster (SessionQueueReaderRosterTests)

Both handlers left ExemptReaders entirely: they no longer read session.NowPlayingQueue at all (their queue access routes through the shared writer, the same shape JF-582 gave Next/Previous), so the reader scan no longer discovers them and a FUTURE direct queue read in either handler fails the roster by name. The ProgressReporter owner exemption gained the third internal guard caller (RehydrateAndEnqueueToBothStores) and was strengthened per-method: every listed owner member is asserted to exist AND to reach the guard, directly or through another owner member (ServeAdjacentQueueItem composes the private combined helper, the direct caller), so the chain cannot detach from the guard silently.

## Tests (red-first)

5 handler tests in QueueRehydrationAdoptionTests: AddToQueue wiped+coherent (add lands in both stores, persisted file reloaded through a fresh manager proves restart survival, NO launch directive, session mirrors device order), AddToQueue stale-queue (keeps today's launch + one-item session; the device store still takes the insert per the both-stores contract), AddToQueue normal-shape (both-stores widening pin), PlayNext wiped+coherent (after-current insert in both stores at index 2, pointer intact, persisted), PlayNext stale-queue (keeps today's launch; front insert in the device store). ALL 5 observed RED on both TFMs before the handler change (each failed exactly the JF-578 way: a ReplaceAll launch directive instead of the queue-add tell, and a device store missing the insert), GREEN after. Plus 7 Enqueue unit tests in DeviceQueueManagerTests (End/AfterCurrent placements, pointer advance on front-insert shapes, seed, shuffle snapshot + RestoreOrder retention, persistence round-trip).

## Verification

Full suite `dotnet test` (no --no-build): 4044/4044 passed on net9.0 AND net10.0 (final run, after every code edit; +12 tests over the JF-579 baseline: 5 handler adoption pins + 7 writer unit tests). `dotnet build -c Release` (full solution): 0 warnings, 0 errors. Files changed: DeviceQueueManager.cs, ProgressReporter.cs, AddToQueueIntentHandler.cs, PlayNextIntentHandler.cs, QueueRehydrationAdoptionTests.cs, DeviceQueueManagerTests.cs, SessionQueueReaderRosterTests.cs, this task file.

## Risks / accepted consequences

- Mirror-leg order consequence: when the session queue is a superset of the device membership (progressive continuation tracks), the added item lands after the device items but BEFORE the session-only tail. This is the same consequence every existing mirror (shuffle-on, rehydration) already has; the device order is authoritative (JF-447).
- Stale-queue shape: the add is inserted into the persisted queue even when the guard declines rehydration (the user asked to extend their queue; the persisted store IS that queue). A later restart while the added item plays can then rehydrate the older items behind it: the JF-574 accepted trade-off family (membership is the best available coherence signal).
- Non-wiped widening, identical to the JF-577/579 adoptions: a populated session queue with a null now-playing item and a coherent member token now counts as "current" (the add tells instead of launching).
- DOD items 9/10 (/simplify, /code-review) intentionally left to the orchestrator's gates per the task instructions (the JF-577/JF-579 precedent).
<!-- SECTION:NOTES:END -->
