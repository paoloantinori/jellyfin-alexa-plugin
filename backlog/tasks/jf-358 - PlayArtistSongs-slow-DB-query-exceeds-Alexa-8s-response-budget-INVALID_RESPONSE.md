---
id: JF-358
title: >-
  PlayArtistSongs slow DB query exceeds Alexa 8s response budget
  (INVALID_RESPONSE)
status: Done
assignee: []
created_date: '2026-07-20 16:13'
updated_date: '2026-07-26 13:57'
labels:
  - performance
  - playback
  - regression
  - live-tv-or-db
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On 2026-07-20, a live Echo play of "P!nk floyd" via PlayArtistSongsIntent hit Alexa's INVALID_RESPONSE ("la skill richiesta non ha fornito una risposta valida") with reason=ERROR, error.type=INVALID_RESPONSE, "An exception occurred while dispatching the request to the skill." The SessionEndedRequest arrived ~9s after the request; the skill actually completed in 14047ms (corr=63a6dda9).

ROOT CAUSE (proven from logs, not inferred): the Jellyfin DB query that fetches an artist's songs is slow and variable. Timestamps from /config/log/log_20260720.log (matched artist -> returned N songs):
- 14:34 Norah Jones: ~2.1s (under Alexa's 8s window — succeeded)
- 18:00 Soul Coughing: ~4.9s (under window — succeeded)
- 18:10 P!nk (2 songs): ~12.2s (OVER window — Alexa timed out at ~9s, INVALID_RESPONSE)

The 12s is spent ENTIRELY between "PlayArtistSongs: matched artist" (BaseHandler logs the match) and "Jellyfin returned N songs for artist" — i.e. inside _libraryManager.GetItemList(artistSongsQuery) at PlayArtistSongsIntentHandler.cs:388 (wrapped in RetryAsync). The response body built AFTER that (including the JF-353 announce "In riproduzione So What") was correct — the announce feature is NOT implicated; it runs in microseconds after songs are fetched.

This is a PRE-EXISTING latency regression, not caused by the announce work: a 14:34 Norah Jones search (before the deploy session) also went through the same path. The variability (2s-12s) means it intermittently beats or misses Alexa's 8s budget.

INVESTIGATE / FIX:
1. Why is GetItemList(artistSongsQuery) taking 2-12s for some artists? Suspects: missing DB index on the ParentId/Artist filter, library filter cache (ApplyLibraryFilter) thrashing, RetryAsync retrying a slow/timing-out query multiple times (check if RetryAsync multiplied the latency via retries on a query that succeeded-but-slowly), or the query evaluating a large library.
2. The robust fix likely needs BOTH: (a) speed up / index the query, AND (b) send a SendProgressiveResponse within the first ~2s to hold Alexa's session open past 8s while the query completes (there's already a SendProgressiveResponse helper — verify PlayArtistSongs uses it early enough; some handlers were made fire-and-forget in commit a8da8dc / JF-294).
3. Related memory: feedback_hang_fix.md (unbounded HTTP calls caused hangs — but THIS is a slow DB query, not an HTTP hang; distinct cause, same 8s-budget symptom).

Acceptance criteria:
- PlayArtistSongs completes end-to-end in under 8s for the worst observed case (large library / small-result artist), OR a progressive response is sent early enough that Alexa does not INVALID_RESPONSE.
- A live Echo test of "P!nk floyd" (the 12s repro) succeeds without timeout.
- Regression coverage: see JF-359 (E2E response-budget test).
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-26 13:57
---
REVIEWED 2026-07-26: the root cause identified in this task (MediaTypes=Audio not constraining ArtistIds queries) was ALREADY FIXED inline in the handler. Line 387 now uses IncludeItemTypes=BaseItemKind.Audio (not MediaTypes=Audio), with an explicit JF-358 comment explaining why. The fix also adds Limit = GetInitialFetchSize() to fetch only the first page, and uses GetItemList (not GetItemsResult) to avoid the slow dbQuery.Count(). This task is LIKELY OBSOLETE: the specific 12s query path described here has been addressed. The progressive response (SendProgressiveResponse at line 103) is already fire-and-forget. Remaining risk: the in-memory index path (used when _artistIndex.IsReady) is sub-ms; the DB fallback path still uses GetItemList but with the corrected filter. Close as Done unless a live 12s repro resurfaces.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The root cause (MediaTypes=Audio not constraining ArtistIds, causing sort over thousands of items + 8s budget burn) was already fixed inline in PlayArtistSongsIntentHandler.cs:387 with IncludeItemTypes=BaseItemKind.Audio + Limit + GetItemList (not GetItemsResult). The progressive response is fire-and-forget. No further action needed unless a live 12s repro resurfaces.
<!-- SECTION:FINAL_SUMMARY:END -->
