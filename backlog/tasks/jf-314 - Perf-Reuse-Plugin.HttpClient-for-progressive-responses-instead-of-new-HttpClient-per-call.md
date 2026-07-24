---
id: JF-314
title: >-
  Perf: Reuse Plugin.HttpClient for progressive responses instead of new
  HttpClient per call
status: Done
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-07-15 07:35'
labels:
  - performance
  - quick-win
milestone: m-7
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:989'
  - 'Jellyfin.Plugin.AlexaSkill/Plugin.cs:92'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`BaseHandler.cs:989` does `using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) }` on the progressive-response path — hit once per handled request — despite an `IHttpClientFactory`-backed `Plugin.HttpClient` existing (`Plugin.cs:92`, with a static fallback). This is the classic socket-exhaustion antipattern (sockets stuck in TIME_WAIT under load). Verified 2026-07-12: this is the only `new HttpClient` in production code.

Fix: use `Plugin.HttpClient`. Watch the per-call 2s timeout — the shared client's timeout is global, so either pass a `CancellationToken` with a 2s deadline to the send, or use a per-request timeout mechanism rather than mutating the shared client's Timeout property (which is not safe to change per-call). Verify progressive responses still cut off at ~2s.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Progressive-response sending uses the shared Plugin.HttpClient (or factory) rather than constructing a new HttpClient per call
- [ ] #2 The ~2s progressive-response deadline is preserved via CancellationToken/per-request timeout, not by mutating the shared client's Timeout
- [ ] #3 No new HttpClient instances remain in production code (grep clean)
- [ ] #4 Existing progressive-response tests pass; a test covers the timeout path
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The eea9cea switch to Plugin.HttpClient was production-safe (IHttpClientFactory returns a fresh client per call, so ProgressiveResponse's BaseAddress-set does not throw — verified via a runtime probe of Alexa.NET 1.22.0) but it dropped the original 2s Timeout (SendSpeech takes no CancellationToken). Fixed by routing progressive responses through a dedicated factory-backed AlexaSkillProgressive named client (Timeout=2s via ConfigureHttpClient) exposed as Plugin.HttpClientProgressive; factory path stays fresh-per-call, fallback is a per-call client (required, since a static client would throw on 2nd use). Added 2 tests (2s timeout, distinct instances per call). Closed in commit c4a5bd7.
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
