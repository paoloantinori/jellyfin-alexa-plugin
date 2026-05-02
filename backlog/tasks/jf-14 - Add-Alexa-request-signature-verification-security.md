---
id: JF-14
title: Add Alexa request signature verification (security)
status: Done
assignee: []
created_date: '2026-05-01 06:02'
updated_date: '2026-05-01 07:15'
labels:
  - security
  - robustness
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CRITICAL security gap: The Alexa endpoint does not verify request signatures from Amazon. Any system that can reach the endpoint could send fake Alexa requests.

Implementation:
- In AlexaSkillController.HandleIntentRequest(), read raw body (not [FromBody] dynamic)
- Extract SignatureCertChainUrl and Signature headers
- Use Alexa.NET.Request.RequestVerification.Verify(signature, certUri, body) to validate
- Return 401 if verification fails
- Required for Alexa skill certification for self-hosted endpoints

Reference: https://developer.amazon.com/en-US/docs/alexa/custom-skills/host-a-custom-skill-as-a-web-service.html#check-request-signature
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added Alexa request signature verification to prevent unauthorized requests:

**Changes (AlexaSkillController.cs):**
- Removed `[FromBody] dynamic json` parameter -- now reads raw body via `StreamReader(Request.Body)` to preserve exact bytes for signature verification
- Added `VerifyAlexaSignature()` method that extracts `Signature` and `SignatureCertChainUrl` headers and validates using `Alexa.NET.Request.RequestVerification.Verify()`
- Returns SkillResponse with error message if verification fails (no 401/403 -- must be valid SkillResponse per Alexa spec)
- Handles missing headers and verification exceptions with logging
- Added `using System.Linq` and alias `using RequestVerification = Alexa.NET.Request.RequestVerification` to resolve namespace conflict with `Jellyfin.Plugin.AlexaSkill.Alexa`

**Verification:** Build 0 errors, 130/130 tests pass
<!-- SECTION:FINAL_SUMMARY:END -->
