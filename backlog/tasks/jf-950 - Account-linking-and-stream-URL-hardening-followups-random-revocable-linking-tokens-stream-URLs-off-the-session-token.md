---
id: JF-950
title: >-
  Account-linking and stream-URL hardening followups: random revocable linking
  tokens, stream URLs off the session token
status: To Do
assignee: []
created_date: '2026-11-23 13:54'
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
