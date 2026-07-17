---
id: JF-312
title: >-
  Security hardening: single-use thread-safe CSRF tokens, plaintext-token
  documentation, debug-log redaction
status: To Do
assignee: []
created_date: '2026-07-12 14:57'
updated_date: '2026-07-13 20:18'
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
- [ ] #1 CSRF token is invalidated/consumed on first successful validation and cannot be reused
- [ ] #2 CSRF token store is thread-safe under concurrent access
- [ ] #3 README/docs include a security note about plaintext token storage in plugin config and its backup/file-access implications
- [ ] #4 Debug logging of Alexa request bodies redacts access tokens and apiAccessToken
- [ ] #5 Unit tests cover CSRF single-use and (where feasible) the redaction helper
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
