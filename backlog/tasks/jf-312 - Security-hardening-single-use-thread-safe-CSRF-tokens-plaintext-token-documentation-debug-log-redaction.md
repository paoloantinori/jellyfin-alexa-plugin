---
id: JF-312
title: >-
  Security hardening: single-use thread-safe CSRF tokens, plaintext-token
  documentation, debug-log redaction
status: Done
assignee:
  - claude
created_date: '2026-07-12 14:57'
updated_date: '2026-07-19 20:36'
labels:
  - security
  - hardening
milestone: m-6
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/Handler/CsrfTokenHandler.cs
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:338'
  - 'Jellyfin.Plugin.AlexaSkill/Entities/User.cs:42'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three low-severity hardening items from the 2026-07-12 security review (verified against code):

1. CSRF tokens are not single-use and the store is not thread-safe. `Controller/Handler/CsrfTokenHandler.cs` `ValidateCsrfToken` returns true without consuming the token (only expired ones are removed), so a token is replayable until expiry; the backing `Dictionary` is mutated from concurrent requests without locking. Fix: consume the token on successful validation; use a thread-safe store (e.g. `ConcurrentDictionary`).

2. Secrets stored in plaintext in plugin config XML: `Entities/User.cs:42,53` (`JellyfinToken`, `SmapiRefreshToken`), `Lwa/DeviceToken.cs:37,42` (LWA tokens), `Configuration/PluginConfiguration.cs:79` (`LwaClientSecret`). This is standard Jellyfin behavior (config is admin-only) but long-lived SMAPI/Jellyfin credentials in cleartext deserve an explicit security note in the README/docs so operators understand backup/file-access exposure. (Encryption at rest is optional/larger scope — document first.)

3. Debug logging writes the full Alexa request body (contains access token = user GUID, apiAccessToken) at `AlexaSkillController.cs:338`. Off by default (Debug level) but enabling Debug for triage writes tokens/user identifiers to logs. Fix: redact tokens/PII when logging request bodies.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 CSRF token is invalidated/consumed on first successful validation and cannot be reused
- [x] #2 CSRF token store is thread-safe under concurrent access
- [x] #3 README/docs include a security note about plaintext token storage in plugin config and its backup/file-access implications
- [x] #4 Debug logging of Alexa request bodies redacts access tokens and apiAccessToken
- [x] #5 Unit tests cover CSRF single-use and (where feasible) the redaction helper
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-19 20:36
---
DELIVERED (commits 4c1aec6 + f0b365c). (1) CSRF: ValidateCsrfToken is now single-use (consumes the token via an atomic TryRemove -- the review caught the TryGetValue+TryRemove two-step race where a double-clicked submit could validate twice) + the store is a ConcurrentDictionary (thread-safe). The account-linking page renders a fresh token per GET, so single-use is compatible. (2) Debug-log redaction: a new RequestLogRedactor masks accessToken/apiAccessToken/consentToken/userId in the request body (the review added consentToken + JSON-escape handling), plus a new RedactUrl masks the api_key query param at the two stream-URL debug-log sites (BuildAudioPlayerResponse streamUrl + LiveTvStreamResolver dynamic-HLS fallback) that leaked the Jellyfin token credential. (3) README: a security note on plaintext token storage in plugin config (backup/file-access implications). Tests: CSRF single-use; redactor masks the sensitive fields + escaped quotes; URL api_key redaction. /code-review high (opus) found 5 issues, all fixed (f0b365c). Full suite 2542/2542. Deployed + sanity-verified (config intact, pink floyd plays).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Three security-hardening items. (1) CSRF tokens are now single-use (atomic TryRemove consume) + thread-safe (ConcurrentDictionary), closing a replay + race. (2) Debug logging redacts the Alexa request body (accessToken/apiAccessToken/consentToken/userId) + stream-URL api_key via a new RequestLogRedactor/RedactUrl, so enabling debug logging for triage no longer leaks credentials. (3) README documents plaintext token storage in plugin config + its backup/file-access implications. /code-review high found + fixed 5 issues (CSRF consume race, consentToken omission, JSON-escape handling, stream-URL api_key leak). Tests cover CSRF single-use + the redaction. Full suite 2542/2542. Deployed.
<!-- SECTION:FINAL_SUMMARY:END -->
