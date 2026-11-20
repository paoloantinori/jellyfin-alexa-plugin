---
id: JF-929
title: 'Log PlaybackFailed error.type and error.message (issue #45 diagnostic ask)'
status: Done
assignee: []
created_date: '2026-11-20 08:30'
updated_date: '2026-11-20 10:27'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/45'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Issue #45 (open regression report) asks for this explicitly: our PlaybackFailed log line prints only token and offset ("Playback failed for item {ItemId} at offset {OffsetMs}ms", PlaybackFailedEventHandler.cs:57-62) and drops the request's Error payload. Amazon sends error.type (e.g. MEDIA_ERROR_SERVICE_UNAVAILABLE vs MEDIA_ERROR_INTERNAL_DEVICE_ERROR) and error.message on every PlaybackFailed; logging them is the discriminating observation point between a device-rejected directive (no HTTP fetch at the origin) and a stream fetch failure, which is the exact fork in the #45 triage. Scope: extend the existing LogError call to include the error type and message; verify the Alexa.NET request model exposes them (AudioPlayerRequest/PlaybackFailedRequest Error member) and read them through the concrete type if the shared base does not carry the fields. Everything else in the handler stays byte-identical.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The PlaybackFailed LogError line includes the request's error type and error message alongside the existing ItemId/Offset fields
- [ ] #2 A unit test feeds a PlaybackFailed request carrying a known Error payload and asserts both values reach the logged message
- [ ] #3 No other handler behavior changes: the response stays the keep-alive ack, the JF-655 flag-clear logic and the ReportStopOrderedAsync call are untouched
- [ ] #4 Build is 0 warnings on both TFMs and the full suite passes without --no-build
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
