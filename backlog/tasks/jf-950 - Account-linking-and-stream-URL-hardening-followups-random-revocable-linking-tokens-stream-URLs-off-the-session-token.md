---
id: JF-950
title: >-
  Account-linking and stream-URL hardening followups: random revocable linking
  tokens, stream URLs off the session token
status: In Progress
assignee: []
created_date: '2026-11-23 13:54'
updated_date: '2026-11-28 08:17'
labels:
  - security
  - hardening
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Defense-in-depth followups to the account-linking hardening (details intentionally neutral here; add the advisory reference after coordinated publication): (1) ISSUE RANDOM REVOCABLE LINKING TOKENS instead of the current identifier-based access token at the implicit-grant response (32 bytes from RandomNumberGenerator, stored hashed, mapped to the user, with a revoke action in the config UI). Existing links need a migration path or one-time re-link. The skill-binding gate in the request controller is the primary protection; this removes the identifier-as-token shape entirely so future request paths cannot regress into accepting it. (2) MIGRATE STREAM URLS off the stored session token: PlaybackLaunchBuilder and LiveTvStreamResolver embed the user's Jellyfin session token in stream URLs that transit Amazon and reverse-proxy access logs; the signed, item-scoped token mechanism already used by the video-audio endpoints (10h TTL, item-scoped) covers stream auth without exposing a long-lived session. Sequence after the release: (2) first (mechanical, the infra exists), then (1) (needs migration UX).
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
ITEM (2) IN PROGRESS (2026-11-27 sera): worker dispatched with the proxy design (StreamProxyController with HMAC gate + Range passthrough + loopback fetch via InternalServerUrl config, default http://localhost:8096; StreamProxyEnabled flag DEFAULT TRUE as the rollback switch; BuildStreamUrl + GetImageUrl migrated; LiveTV fallback OUT of scope - the dynamic-HLS master playlist embeds api_key segment URLs and needs playlist rewriting, its own change; PlaybackInfo call is server-side; IPTV direct-remote carries no session token). Maintainer constraint: NON ROMPERE NULLA - playback hot path; verification plan: full suite + gates + live device play after deploy.

ITEM (2) DEPLOYED AND LIVE-VERIFIED (2026-11-28 08:0x): DLL from HEAD deployed (md5 a7d50461 match, config survived 1 user, tokens intact). LIVE EVIDENCE: the simulator's play directive carries the PROXY URL (/alexaskill/api/stream/audio/{id}?token=HMAC, NO api_key anywhere in the device-facing URL); a real fetch through the proxy returns 206 audio/mpeg 1024 bytes on a Range request (the seek path); the no-token request 401s (the gate); the debug log line shows the resolved item (Blue Train, Audio) and the Audio static endpoint branch; zero plugin errors on startup. Both review rounds' findings all applied (the critical one: the stream KIND is caller-declared via the audio/video route, never derived from MediaType - podcast Episodes route /Audio as they always did). Remaining for item 2: an on-device play (the Echo fetch) as the final end-to-end check, and the LiveTV fallback as its own future change (playlist rewriting). Item (1) (random revocable linking tokens) remains open with its migration-UX prerequisite.

DEVICE ROUND COMPLETE (2026-11-28 09:16, maintainer-confirmed 'questa volta ha funzionato'): the first live attempt failed with MEDIA_ERROR_SERVICE_UNAVAILABLE - root cause found and fixed live: the Echo AudioPlayer fetcher HEAD-probes stream URLs (the legacy Jellyfin static endpoints answer HEAD 200; our GET-only routes 405'd at the routing layer, zero action logs). Fix: explicit templated [HttpHead] on all three proxy routes (the template-less attribute does not combine - Allow: GET in the 405 proved it), rebuilt, redeployed. SECOND live attempt: full chain verified in the logs (09:16:48 StreamProxy proxying -> 09:16:52 PlaybackStarted playerActivity=PLAYING -> maintainer paused at 11.3s offset, PlaybackStopped). The session token is now absent from every URL the device sees, verified end-to-end on real hardware. Lesson recorded in memory echo-head-probes-stream-urls (likely also explains a latent constraint on the JF-309 endpoints if ever routed through AudioPlayer).
<!-- SECTION:NOTES:END -->
