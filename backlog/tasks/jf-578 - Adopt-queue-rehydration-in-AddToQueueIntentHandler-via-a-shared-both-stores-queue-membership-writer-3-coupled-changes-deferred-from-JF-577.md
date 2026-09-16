---
id: JF-578
title: >-
  Adopt queue rehydration in AddToQueueIntentHandler via a shared both-stores
  queue-membership writer (3 coupled changes, deferred from JF-577)
status: To Do
assignee: []
created_date: '2026-09-16 13:49'
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
