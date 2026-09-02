---
id: JF-419
title: >-
  Cold-start artist index: skill times out on first request after DLL deploy —
  communicate warming state to user
status: Done
assignee:
  - zai
created_date: '2026-08-31 06:04'
updated_date: '2026-09-01 05:59'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-08-31 07:59: after a DLL deploy (container restart), the first user request received 'la skill richiesta non ha fornito una risposta valida' (INVALID_RESPONSE). The handler received the request (PlayArtistSongsIntent, musician=P!nk floyd) at 07:59:37 but produced no response within 12 seconds. No handler execution logs appear (no 'PlayArtistSongs: entered', no ArtistSearch tier logs) — the handler likely hung on the cold database path while the artist index was still loading in the background.

The artist index service (SongNgramIndexService, ArtistIndexService) loads all Audio items at startup. During this loading window (which can be minutes for large libraries), artist searches fall through to the SQLite database path. On a freshly restarted container with SQLite cache cold, these queries can be extremely slow, exceeding Alexa's ~8-second response window.

The user gets an unhelpful error and doesn't know the skill is still starting up.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 After a DLL deploy (plugin update/restart), the artist index (ArtistIndexService) takes time to load (loading all Audio items). During this cold-start window: (a) searches fall through to the database path which may be slow, (b) requests may exceed Alexa's 8-second window causing INVALID_RESPONSE
- [ ] #2 The plugin should DETECT the cold-start state (artistIndex.IsReady == false or artistIndex.Count == 0) and respond with a user-friendly message instead of potentially timing out: 'Mi sto ancora preparando, riprova tra un minuto' (or equivalent locale string)
- [ ] #3 The cold-start message must be a Tell response (session-ending, no retry loop) so the user knows to wait and try again
- [ ] #4 If the artist index IS ready but the search is still slow (> 3 seconds), log a warning with the timing so cold-start vs hot-path can be distinguished in triage
- [ ] #5 ResponseStrings key added to all 17 locales
- [ ] #6 Unit tests: handler responds with the cold-start message when artistIndex.IsReady is false
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-419 (parent) closed: the post-deploy cold-start window no longer risks INVALID_RESPONSE on any search path.

All three subtasks landed with gates:
- JF-419.1 (36f11af): ArtistIndexService self-recovers from a failed startup load (one-shot re-arming retry timer, disarm on success, hardened dispose ordering).
- JF-419.2 (e14b0cc): the warming gate became a two-layer mechanism - per-handler entry guards where the primary path queries the cold DB + the ArtistSearch choke point throwing SkillWarmingUpException, translated once in the request pipeline (covers controller FindSong-session route, handler loop, SimulatorController). All 10 entry points covered; metrics/logging interceptors preserved via SkipColdLibraryWork.
- JF-419.3 (0e14aec): gates are per-index (artist vs song n-gram, per request path), both index services share the hardened DebouncedLibraryIndexService lifecycle (debounce + retry + dispose ordering), a give-up path prevents an endless warming refusal when an index load persistently fails (degrade to bounded DB paths, self-re-enable on a later successful refresh), and the song index has its own layer-2 choke point.

Live-verified post-deploy (JF-419.2 bundle, DLL b79870d9): warming refusals log with intent+correlation, normal operation identical when ready. The JF-419.3 bundle ships on the next deploy.

Deployed state: JF-419.2 live on minix; JF-419.3 pending next deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

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
