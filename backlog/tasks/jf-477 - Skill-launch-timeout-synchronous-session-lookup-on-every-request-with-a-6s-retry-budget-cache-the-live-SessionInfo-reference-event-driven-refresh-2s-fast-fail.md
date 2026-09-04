---
id: JF-477
title: >-
  Skill launch timeout: synchronous session lookup on every request with a 6s
  retry budget (cache the live SessionInfo reference + event-driven refresh + 2s
  fast-fail)
status: In Progress
assignee: []
created_date: '2026-09-04 04:38'
updated_date: '2026-09-04 04:38'
labels: []
dependencies: []
references:
  - >-
    Live incident 2026-09-03 20:06 corr=40edec8a (8s silent gap, watchdog
    timeout)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs HandleRequestAsync
    ~:410
  - CLAUDE.md coverage caveat (the response-window discipline)
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-03 20:06 live incident (corr=40edec8a): Paolo's skill launch timed out on-device ('ci ho messo troppo tempo'). Log evidence: the request entered the pipeline at 20:06:07, produced NO log lines for 8 seconds, the handler's first line landed at 20:06:15 the same instant the controller watchdog fired the 144-byte timeout response. The only await in that stretch is SessionManager.GetSessionByAuthenticationToken in BaseHandler.HandleRequestAsync (line ~410), wrapped in RetryHelper with the full AlexaRequestTimeoutMs=6000 budget: the lookup burned the entire budget silently (retries log nothing until final failure) and the 8s Alexa window expired before the handler could run. The Sessions endpoint was healthy immediately after (13ms, 2 sessions): a transient IO/DB hiccup, but the architecture turned one slow call into a user-visible total failure because the same synchronous lookup runs on EVERY request of EVERY dialog turn with no cache.

Fix design (two layers, both in this task):
1. SESSION REFERENCE CACHE keyed by (JellyfinToken, DeviceId) holding the LIVE SessionInfo reference (Jellyfin SessionInfo objects are the live per-session objects held by the SessionManager, so the reference stays current; re-fetching by token is the expensive scan). TTL ~60s for hygiene, PLUS event-driven refresh from the AudioPlayer events the plugin already receives (PlaybackStarted/Stopped carry the session context: warm the cache outside the request window so request-time lookups are cache hits). Stale-session safety: on a play-path failure that indicates a dead session, invalidate the entry and refetch once (single retry), then degrade to the existing not-found path.
2. FAST-FAIL BUDGET on the REQUEST-PATH lookup: cut the RetryHelper timeout for this call from AlexaRequestTimeoutMs to a dedicated ~2000ms (a session lookup that cannot answer in 2s cannot serve the request anyway); the full budget may remain on the warm/refill path where no user is waiting. This is the belt: even a cache-miss during an IO hiccup costs 2s and yields the coherent UserNotFound-style degradation instead of eating the window.

Out of scope (documented as future evolution if the cache proves insufficient): making SessionInfo resolution lazy for handlers that never touch it (the launch resume offer only needs the client-side AudioPlayer token and the plugin's in-memory DeviceQueueManager state); that is a wide refactor over the concrete SessionInfo type.

Acceptance criteria:
- A cache hit path issues NO GetSessionByAuthenticationToken call (assert via mock call counts: second request with same token+device reuses the reference).
- PlaybackStarted refreshes the cache entry for that (token, device) without a request-path lookup.
- Request-path lookup respects the reduced budget (a hanging first call fails within ~2s with the coherent response, not at 8s).
- A dead-session play failure invalidates and retries once (unit test with a sequence: live ref then dead-session then fresh ref).
- Live verification after deploy: two consecutive simulator/device launches, the second with zero session-lookup log latency; the logs show the cache hit.
- Suite green, gates run.
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
