---
id: JF-309
title: >-
  Security: Scope video-audio streaming endpoints with a signed item token
  (unauth access-control bypass)
status: Done
assignee: []
created_date: '2026-07-12 14:57'
updated_date: '2026-07-21 16:09'
labels:
  - security
  - access-control
milestone: m-6
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:804'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every video-audio endpoint in `Controller/VideoAudioController.cs` is `[AllowAnonymous]` (audiobook stream.m3u8, segments, MP4/HLS mux — around lines 128, 289, 425, 710). `ValidateVideoAudioRequest` (~804-849) only checks that the item GUID parses, exists, and is `IHasMediaSources`. There is no per-user library filter, no content-access/parental gating, and no token. Anyone on the internet who learns a Jellyfin item GUID can stream that item, bypassing Jellyfin's per-user library restrictions. GUIDs are 122-bit (not brute-forceable) but Jellyfin does not treat them as secrets — they leak through many channels.

This is inherent to how the Echo fetches media unauthenticated, so the fix is not simple auth. Mitigate by minting a short-lived, signed, item-scoped token (HMAC over itemId + expiry, server secret) embedded in the stream URL the skill hands to the device, and validating it in these endpoints instead of accepting a bare GUID. Verified against code 2026-07-12.

Note: this touches the same controller as the audiobook HLS resume logic — preserve the segment/playlist behavior (event playlists, ?start= slicing) documented in CLAUDE.md.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Stream/segment/playlist URLs handed to the Echo carry a signed, expiring, item-scoped token
- [ ] #2 Video-audio endpoints reject requests with a missing, malformed, expired, or wrong-item token (HTTP 401/403)
- [ ] #3 A bare-GUID request with no valid token can no longer stream a library item
- [ ] #4 Token TTL is long enough for legitimate long audiobook/video sessions (or is refreshable) — verify a multi-hour playback does not break mid-stream
- [ ] #5 Audiobook resume (?start=) and segment fetching still work end-to-end
- [ ] #6 Unit tests cover token accept/reject paths
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented signed item-scoped stream tokens (HMAC-SHA256, 10h TTL) gating all 4 video-audio endpoints. StreamTokenHelper (Mint/Validate with constant-time compare) + StreamTokenSecret auto-gen in PluginConfiguration. Token minted in 3 URL builders, validated in all 4 endpoints (401 on missing/expired/wrong-item/tampered). Threaded into playlists (ffmpeg-written via RewritePlaylistWithToken, audiobook via WriteAudiobookPlaylist + ServeAudiobookPlaylistAsync rewrite). Single-chapter audiobook re-mints chapter-scoped token (overrideToken). Log redaction (token= masked alongside api_key=). 2574 tests pass. /simplify + /code-review high passed (code review caught 2 CRITICAL bugs: RewritePlaylistWithToken predicate matched zero real segment lines + single-chapter token mismatch — both fixed). Deployed to minix, verified live: bare-GUID → 401 on all endpoints, valid token → 200. E2e test (test_stream_security.py) added.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
