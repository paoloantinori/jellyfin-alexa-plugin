---
id: JF-385
title: >-
  Docs: troubleshooting FAQ for reverse-proxy/Cloudflare install traps
  (geo-IP/bot blocking + Wildcard-vs-Trusted certificate on both sides) from
  issue #18
status: To Do
assignee: []
created_date: '2026-08-20 21:16'
labels:
  - docs
  - faq
  - onboarding
  - cloudflare
  - support
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/18'
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/8'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From issue #18 (closed COMPLETED 2026-08-20): the reporter succeeded only after fixing TWO stacked config mistakes, each of which alone did not suffice - a non-obvious combination that cost a support round-trip:

1. CLOUDFLARE/NETWORK BLOCKING: he blocked every non-German/Dutch IP. Alexa skill requests are server-to-server POSTs from AWS IPs, so every voice request was blocked (fast 403 -> INVALID_RESPONSE at the same second as the request), while browser-based account linking worked - a misleading asymmetry. Same class as Bot Fight Mode (#8). Fix: allow POSTs to the skill endpoint (ideally scoped to the skill path) from any IP / skip bot+geo rules for the Jellyfin hostname.

2. CERTIFICATE MODE MISMATCH: Certificate settings set to 'Trusted Certificate' on BOTH the Amazon skill and the plugin; must be Wildcard on BOTH sides. He tried each fix independently - only together it worked.

Reporter's closing note: 'now its Working fine and very well... the Setup was easy as cake' - the plugin itself was fine; pure onboarding traps.

VALUE: a troubleshooting section saves the next user a round-trip, supports the Plex-migration window (JF-330 audience), and both traps produce misleading symptoms (linking works, voice fails).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Extend the troubleshooting/FAQ (see the v0.11.2.0 changelog FAQ entry) with the two installation traps from #18: (a) Cloudflare/network blocking (Bot Fight Mode, geo-IP rules, WAF) blocks Alexa's AWS server-to-server POSTs - browser linking works while every voice request fails with an immediate 403/INVALID_RESPONSE
- [ ] #2 #2 (b) certificate mode must be Wildcard on BOTH sides (Amazon skill AND plugin); Trusted on either side fails - each fix alone does NOT suffice, only both together
- [ ] #3 #3 Cross-link issues #18 and #8 for provenance; user-facing plain English, no internals
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
