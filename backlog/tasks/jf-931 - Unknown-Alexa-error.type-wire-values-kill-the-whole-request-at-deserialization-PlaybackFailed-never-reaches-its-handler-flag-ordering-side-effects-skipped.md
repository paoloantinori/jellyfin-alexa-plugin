---
id: JF-931
title: >-
  Unknown Alexa error.type wire values kill the whole request at deserialization
  (PlaybackFailed never reaches its handler, flag/ordering side effects skipped)
status: In Progress
assignee: []
created_date: '2026-11-20 09:36'
updated_date: '2026-11-24 16:31'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/45'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-929 code-review round (2026-11-20), empirically reproduced by the reviewer against the plugin's own binaries: Alexa.NET 1.22.0 deserializes error.type through StringEnumConverter over the ErrorType enum with no unknown-value tolerance, so an Amazon wire value absent from the enum (the enum grew from 4 to 37 members historically; Amazon keeps adding values) makes JsonConvert.DeserializeObject&lt;SkillRequest&gt;(body) throw 'Requested value ... was not found' at AlexaSkillController.cs:311. The controller catch (line 433) logs only 'Unhandled exception processing Alexa request [ErrorRef=...]'. Consequences for exactly the failure events diagnostic logging exists for: no PlaybackFailed log line at all, no error.type/message, and the handler's side effects (JF-655 active-audio flag clear via MarkAudioPlaybackStopped, JF-447 report-ordering registration via ReportStopOrderedAsync) are silently skipped, corrupting the re-launch gate state machine. Fix shape to evaluate: a custom JsonConverter for the Error/ErrorType seam that tolerates and surfaces unknown values (enum fallback member or raw-string property), or at minimum raw-body logging in the controller catch so nothing is swallowed silently. Note: issue #45's reporter DID see PlaybackFailed lines, so their events deserialized fine; this is the robustness gap for FUTURE/unknown error types, not the #45 cause. Related cluster: JF-930 (same enum-vs-wire-string vocabulary family).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An unknown-to-Alexa.NET error.type wire value on a PlaybackFailed (or any request carrying an Error payload) still reaches the corresponding handler instead of dying at JsonConvert.DeserializeObject
- [ ] #2 The tolerance mechanism logs enough raw context (raw error.type string or request body excerpt) at the controller catch to be triageable on its own
- [ ] #3 The JF-655 flag clear and JF-447 report-ordering registration are not silently skipped for such events
- [ ] #4 Unit test feeds an unmapped error.type string through the production deserialization call and asserts the event is handled
- [ ] #5 Full suite green on both TFMs without --no-build
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
