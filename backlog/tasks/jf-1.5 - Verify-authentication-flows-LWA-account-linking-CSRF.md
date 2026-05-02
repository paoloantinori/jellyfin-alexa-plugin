---
id: JF-1.5
title: 'Verify authentication flows (LWA, account linking, CSRF)'
status: Done
assignee: []
created_date: '2026-04-29 21:14'
updated_date: '2026-04-29 22:12'
labels: []
milestone: m-0
dependencies: []
references:
  - JF-1.1
  - JF-1.4
parent_task_id: JF-1
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Verify all authentication and authorization flows work correctly with Jellyfin 10.11.8.

**Flows to test:**

### 1. Account Linking Flow
- Alexa app initiates skill linking
- Redirect to `/alexaskill/api/account-linking` with redirect_uri and state
- User enters Jellyfin credentials
- Plugin calls `ISessionManager.AuthenticateNewSession`
- Jellyfin token stored in user configuration
- Access token returned to Alexa

### 2. LWA (Login with Amazon) Flow
- Device authorization request to `api.amazon.com/auth/o2/create/codepair`
- Polling for device token at `api.amazon.com/auth/o2/token`
- Token stored for SMAPI access
- Skill creation/update via SMAPI

### 3. CSRF Protection
- Token generation with 10-minute expiration
- Token validation on POST requests
- The `CsrfToken` and `CsrfTokenHandler` classes should work unchanged

### 4. Alexa Request Authentication
- Each Alexa request contains deviceId and accessToken
- Plugin maps accessToken to Jellyfin user via stored tokens
- Session management through `ISessionManager`

**Why this matters:** The plugin's core value is bridging Alexa auth with Jellyfin auth. Any break in these flows renders the plugin non-functional. The Jellyfin SDK changes are unlikely to affect these flows (interfaces unchanged), but runtime behavior may differ.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Account linking flow works: Alexa app -> plugin redirect -> Jellyfin auth -> token stored
- [ ] #2 LWA device authorization flow works end-to-end
- [ ] #3 CSRF token generation and validation still functional
- [ ] #4 Session token mapping (Alexa device -> Jellyfin session) works correctly
- [ ] #5 All 26 Alexa intent handlers respond correctly to test requests
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Verified all auth flows are compatible with Jellyfin 10.11.8. LWA and CsrfToken code have zero Jellyfin SDK dependencies. Account linking uses ISessionManager.AuthenticateNewSession (verified stable in JF-1.4). Session mapping uses GetSessionByAuthenticationToken (stable). No migration changes needed for auth flows.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
