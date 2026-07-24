---
id: JF-268
title: 'Security: Fix command-line injection in VideoAudioController.cs (CodeQL #143)'
status: Done
assignee:
  - claude
created_date: '2026-06-07 21:05'
updated_date: '2026-06-07 21:37'
labels:
  - security
  - codeql
dependencies: []
references:
  - >-
    https://github.com/paoloantinori/jellyfin-alexa-plugin/security/code-scanning/143
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CodeQL alert #143 (critical severity, CWE-78/CWE-88) flags uncontrolled command line in `VideoAudioController.cs:319`. The `arguments` parameter passed to `ProcessStartInfo.Arguments` derives from user-provided values (e.g. itemId paths).

The existing `#pragma warning disable CA3006` suppression claims "itemId is validated as GUID" but CodeQL still flags it as externally controlled. Need to either:
1. Hard-validate/sanitize all inputs that flow into `arguments` before reaching `RunFfmpegAsync`
2. Use argument list (`ArgumentList`) instead of a single string to prevent shell injection
3. Properly suppress with evidence if the flow is truly safe

Reference: https://github.com/paoloantinori/jellyfin-alexa-plugin/security/code-scanning/143
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed CodeQL alert #143 (critical, CWE-78/CWE-88): replaced ProcessStartInfo.Arguments with ArgumentList, passing each ffmpeg argument as an individual OS-level token. This eliminates shell interpretation and command-line injection risk. Also fixed a pre-existing bug where -framerate was mispositioned for the lavfi (black frame) input. Verified on live instance: both art and black-frame paths return HTTP 200.
<!-- SECTION:FINAL_SUMMARY:END -->
