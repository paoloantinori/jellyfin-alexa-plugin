---
id: JF-492
title: >-
  'cerca un album chiamato X' fill shape WITHOUT the calling word
  (musician='surfer rosa') dead-ends on the album-by-artist path: artist-miss +
  empty album needs an album-title retry
status: In Progress
assignee: []
created_date: '2026-09-05 09:49'
updated_date: '2026-09-05 10:28'
labels:
  - nlu-fill-drift
  - play-album
dependencies: []
references:
  - corr=7a54cdf1
  - corr=a40173fb
  - JF-489
  - JF-490
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-05 device test (corr=7a54cdf1, 11:45:01): 'cerca un album chiamato surfer rosa' arrived as PlayAlbumIntent with slots album=EMPTY, musician='surfer rosa' (NO calling word: the statistical fill consumed 'chiamato' entirely this time). 32 seconds earlier the SAME utterance family filled musician='chiamato surfer rosa' (corr=a40173fb) and the JF-489 guard recovered it; this second fill shape has no calling-word prefix, so the guard does not fire, the album-by-artist path searches artist 'surfer rosa', finds nothing, and dead-ends with 'non ho trovato nessun album dell'artista surfer rosa'. Live evidence that the chiamato-family fill drifts BETWEEN REQUESTS, not just across rebuilds (the JF-490 fixture comments documented cross-rebuild drift; this is same-session drift).

FIX SHAPE (handler-side, mirrors the JF-489 pattern): in PlayAlbumIntentHandler's musician branch, when the artist search returns ZERO artists and the album slot is empty, retry the searched musician value as an ALBUM TITLE (one bounded BuildAlbumQuery, same query the JF-489 retry uses) before speaking NotFoundAlbumByArtist: a hit plays the album (announced), a miss keeps today's not-found naming the searched value. Scope it strictly to the artist-MISS case so the JF-411 artist-hit flows and their no-albums messaging stay untouched; use the same value the artist search consumed (post any calling-word strip, so the JF-489 hit path never double-queries).

Unit tests: musician-only with a zero-artist miss + album-title hit plays the album; miss on both keeps the not-found speech naming the musician; artist-hit paths unchanged; JF-489 calling-word shapes unchanged (no double query).
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

## Implementation Notes (2026-09-05)

**JF-492 handler fix** (`PlayAlbumIntentHandler.cs`): the retry lives at the top of
the `artists.Count == 0` branch inside the musician block (comment block starts at
line 256, retry query at line 280), BEFORE the `NotFoundAlbumByArtist` Tell. It
fires only when the album slot is empty (the both-slots artist-miss keeps today's
not-found, pinned by a scope test) and retries the exact value the artist search
consumed (`musician`, post any JF-489 calling-word strip) through one bounded
`BuildAlbumQuery(..., artistIds: null)` under `RetryAsync`
(label `GetAlbumsArtistMissTitleRetry`). A hit clears `musician`, sets `album` to
the searched value, and feeds the results through the SAME mechanism the JF-489
hit path uses: the local renamed `callingWordAlbumResults` ->
`musicianSlotAlbumResults` (now covers both musician-slot title-retry producers;
the downstream skip-the-requery branch and log line were updated accordingly), so
the album play path plays with no re-query and the JF-471/JF-473 gates and the
by-name-and-artist not-found stay out of the way. A miss keeps today's
`NotFoundAlbumByArtist` naming the searched value. The artist-hit tail of the
musician block (`matchedArtist`/`artistsIds`) moved into an `else` so the retry
hit cannot reach `artists[0]` on an empty list; behavior on every artist-hit flow
is unchanged. The JF-489 HIT path never double-queries (the hit clears the
musician before the artist search, so the post-miss retry cannot fire); per the
fix shape, a JF-489 MISS whose artist search also misses does re-run the stripped
value as a title once more (the sanctioned recovery; deterministic miss, one
bounded query). Debug logs follow the spec text:
`PlayAlbum: artist-miss album-title retry '{value}' returned N albums (JF-492)`.

**Unit tests** (`PlayAlbumIntentHandlerTests.cs`, JF-492 section after the JF-489
block): a type-aware `SetupArtistMissCatalog` mock helper (MusicArtist queries
miss, MusicAlbum+SearchTerm hit by map; `SetupTitleSearchByTerm` cannot express
the artist-miss shape because it ignores the item type, so a mapped title would
return from the artist tier-1 query as a bogus artist), plus 5 tests:
artist-miss + title hit plays the album (token + exactly-one-retry-query +
artist-query-first ordering); artist-miss + both miss keeps the not-found naming
the searched value (retry fired exactly once); artist-hit issues no album-title
query at all; JF-489 calling-word hit shape with the Times-style count pin
(exactly ONE album-title query, zero artist queries); both-slots artist-miss
scope pin (no title retry, not-found stays by-artist).

**Sanctioned separate change (JF-488 decision line)**:
`PluginConfiguration.PauseKeepsSession` default flipped false -> true; the doc
comment now records the 2026-09-05 device verification (matrix re-ran clean WITH
the reprompt: zero EXCEEDED_MAX_REPROMPTS in the 3h window, no beep, session
survived 34s and closed USER_INITIATED, in-skill follow-up routing, exact-offset
resume). Tests updated: `PauseKeepsSession_DefaultsToTrue` (renamed from
DefaultsToFalse) and the flag-off pins made explicit in
`PauseIntentHandler_Pause_ReturnsAudioPlayerStopDirective`,
`PauseIntentHandler_DeviceAlreadyStopped_EndsSession`,
`PauseIntentHandler_DeviceFinished_EndsSession` (they previously pinned the old
default implicitly via a fresh config). The stale "default false" comment in
`PauseIntentHandler.cs` was corrected. COMMIT NOTE: this default flip is
independently commit-able from the JF-492 handler fix. One stale comment LEFT
DELIBERATELY: `BaseHandler.BuildPauseResponse`'s doc still says "the flag stays
off by default until then" (BaseHandler.cs ~line 758); that file carries the
concurrent worker's in-flight edits, so it was not touched in this task. Whoever
next edits BaseHandler should drop that sentence.

**Verification** (2026-09-05): `dotnet build Jellyfin.Plugin.AlexaSkill.sln` =
0 errors 0 warnings; `dotnet test Jellyfin.Plugin.AlexaSkill.Tests` (full suite,
with build) = Passed 3262 / Failed 0. No interaction-model or locale changes (no
fixture updates needed). DoD items 9/10 (review gates) remain for the
orchestrator's dispatch flow.

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Simplify dispositions (2026-09-05, orchestrator): the QueryAlbumsByTitleAsync local helper for the two retry sites (JF-489 + JF-492) was evaluated and SKIPPED: the two sites diverge in their logging (LogInformation vs LogDebug) which a query-only helper would not absorb, and the saving is about 8 lines in a 540-line handler. The shared-series-prelude extraction watch-item is recorded on JF-324 (apply it when part 2 adds a third series path, not before).
<!-- SECTION:NOTES:END -->
