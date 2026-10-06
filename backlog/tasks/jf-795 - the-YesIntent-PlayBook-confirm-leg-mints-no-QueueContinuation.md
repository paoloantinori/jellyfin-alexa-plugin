---
id: JF-795
title: >-
  JF-795 - the YesIntent PlayBook confirm leg mints no QueueContinuation (long
  confirm-queued books truncate at the initial fetch size)
status: To Do
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
drift (the AlbumPlayService precedent). Red pin: confirm a 26-chapter book, expect
the continuation minted with TotalCount 26 (pre-fix: null).
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
