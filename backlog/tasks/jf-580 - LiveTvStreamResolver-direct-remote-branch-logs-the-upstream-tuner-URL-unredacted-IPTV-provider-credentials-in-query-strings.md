---
id: JF-580
title: >-
  LiveTvStreamResolver direct-remote branch logs the upstream tuner URL
  unredacted (IPTV provider credentials in query strings)
status: Done
assignee: []
created_date: '2026-09-16 15:39'
updated_date: '2026-09-17 11:30'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit 7f6225b2). LiveTvStreamResolver's direct-remote branch no longer logs the upstream tuner URL verbatim: the new RequestLogRedactor.RedactRemoteUrl drops the ENTIRE query string (provider credential param names are unbounded; a denylist cannot keep up) and strips basic-auth userinfo from the authority (user:pass@host, a standard M3U/IPTV convention the review's adversarial pass caught: the first cut only split on '?'), keeping scheme+host+path. The same review sweep found and fixed two MORE sites of the identical class: PlaybackLaunchBuilder's VideoApp-audio and audiobook-concat launch logs wrote the signed JF-309 stream token verbatim (the sibling audio log at ~1026 already masked; both now route through RedactUrl). The task's second item (the JF-575 capital-ApiKey redactor test gap) closed with a bonus: writing the pin exposed a REAL hole - the mask regex alternation covered api_key and token but NOT the no-underscore ApiKey shape JF-575 introduced, so capital-form URLs had been logging their credential verbatim; the alternation now includes ApiKey (the review verified precisely why the explicit alternative is necessary: IgnoreCase folds case only, the underscore in api_key cannot match ApiKey, and the two alternatives partition the casing space exactly). Tests: 4 redactor tests added (capital mask, remote query drop, userinfo strip with boundary pin, no-query passthrough); the userinfo boundary condition itself was caught red-first by its own test (the first cut's slash-boundary check was off by the scheme's second slash). Gates: /simplify 4-angle + adversarial combined pass, all three findings applied same-turn; suite 4032/4032 both TFMs, Release 0 warnings. Non-findings verified: the fallback log's MediaSourceId/LiveStreamId are not credentials; the PlaybackInfo request URL is never logged; SMAPI/circuit-breaker URLs carry no credentials; no interpolated log call mentions URLs or tokens anywhere in the plugin.
<!-- SECTION:FINAL_SUMMARY:END -->
