---
id: JF-580
title: >-
  LiveTvStreamResolver direct-remote branch logs the upstream tuner URL
  unredacted (IPTV provider credentials in query strings)
status: To Do
assignee: []
created_date: '2026-09-16 15:39'
labels:
  - reliability
  - privacy
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Below-threshold note surfaced by the JF-575 code review (2026-09-16), filed same-turn per the review-discipline rule: LiveTvStreamResolver's direct-remote branch (resolver line ~114) logs the upstream tuner Path verbatim (_logger.LogDebug "direct-remote stream for channel {Id} -> {Url}"), and IPTV provider URLs frequently embed their own credentials (token/user/pass query params). The Jellyfin token is correctly masked (RequestLogRedactor.RedactUrl, IgnoreCase ApiKeyParamRegex), but the SOURCE's credential is not ours to log either. Scope when picked: decide the redaction policy for third-party URLs (redact query strings wholesale for non-Jellyfin hosts, or at minimum apply a generic token-pattern set) and add the capital-ApiKey redactor unit test case noted as a gap in the same review (only lowercase is unit-pinned today).
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
