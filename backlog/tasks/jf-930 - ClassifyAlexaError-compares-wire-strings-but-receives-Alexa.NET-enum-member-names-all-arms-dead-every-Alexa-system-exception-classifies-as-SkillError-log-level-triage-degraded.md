---
id: JF-930
title: >-
  ClassifyAlexaError compares wire strings but receives Alexa.NET enum member
  names: all arms dead, every Alexa system exception classifies as SkillError
  (log-level triage degraded)
status: To Do
assignee: []
created_date: '2026-11-20 09:12'
updated_date: '2026-11-20 09:36'
labels: []
dependencies: []
references:
  - >-
    https://raw.githubusercontent.com/timheuer/alexa-skills-dotnet/7927f5baec3819eb46990a5540b2fe5b0cdc84de/Alexa.NET/Request/Type/Error.cs
  - >-
    https://raw.githubusercontent.com/timheuer/alexa-skills-dotnet/7927f5baec3819eb46990a5540b2fe5b0cdc84de/Alexa.NET/Request/Type/ErrorType.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found during the JF-929 altitude review (out of scope there, filed same turn). The classifier's Alexa-error switch is dead code on its only call path: every Alexa system exception classifies as SkillError.

Evidence (source-verified 2026-11-20):
- Alexa.NET 1.22.0 (pinned by the csproj; nuspec repository commit 7927f5baec3819eb46990a5540b2fe5b0cdc84de): `Error.Type` is the ENUM `Alexa.NET.Request.Type.ErrorType` serialized via StringEnumConverter, and the enum members carry `[EnumMember(Value = "INTERNAL_ERROR")]` etc. (`Alexa.NET/Request/Type/Error.cs` + `ErrorType.cs`). Calling `.ToString()` on the enum returns the MEMBER NAME (e.g. "InternalError"), not the wire value; the StructuredLoggingTests rendering of the same enum proves the member-name output.
- `ExceptionHandler.cs:54` calls `ErrorClassifier.ClassifyAlexaError(exceptionRequest.Error.Type.ToString())`, so the classifier receives "InternalError", "InvalidResponse", ...
- `ErrorClassifier.ClassifyAlexaError` (Alexa/ErrorClassifier.cs, the switch around lines 43-50) compares the WIRE strings "INTERNAL_ERROR", "DEVICE_NOT_CONNECTED", "ENDPOINT_TIMEOUT", "INVALID_REQUEST". No enum member name equals any compared string, so every call lands on `_ => ErrorCategory.SkillError`.
- Note two of the compared wire values (DEVICE_NOT_CONNECTED, ENDPOINT_TIMEOUT) have no ErrorType enum member at all, so string-matching can never be the right shape; classify off the enum (or its EnumMember value) directly.

Consequence: the Timeout and TransientBackend categories are unreachable for Alexa errors; ExceptionEncountered events that are transient/timeout conditions log at the SkillError category's level (Critical) instead of the intended differentiated level. User-facing impact today is none (ExceptionHandler returns the keep-alive ack regardless); the cost is triage signal only.

Test note: StructuredLoggingTests asserts ExceptionHandler logs LogLevel.Critical for ErrorType.InternalError; that expectation stays valid under the fix (INTERNAL_ERROR maps to SkillError either way), so only the currently-dead categories change behavior.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ClassifyAlexaError (or its ExceptionHandler call site) operates on values its arms can actually match: classify off the ErrorType enum directly or off the EnumMember wire value, not a string set that mixes both conventions
- [ ] #2 Unit tests cover each reachable category for the enum members that exist (SkillError always; plus whichever of Timeout/TransientBackend become attainable), including at least one case that previously fell through the discard arm
- [ ] #3 Existing ExceptionHandler log-level expectations in StructuredLoggingTests remain green, or any changed expectation is justified in the task notes
- [ ] #4 Build 0 warnings and full suite pass on both TFMs without --no-build
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
JF-929 code-review round (2026-11-20) folded two sibling findings into this same vocabulary/formatting cluster: (1) three divergent hand-rolled idioms now format the same Alexa Error shape in log lines (ExceptionHandler.cs:60 and SessionEndedRequestHandler.cs:49 pass the raw enum unguarded; PlaybackFailedEventHandler.cs now uses guarded ToString() + UNKNOWN sentinel); a tiny shared formatter (or uniform arg-passing style across the three sites) removes the drift the next handler would copy. (2) The logged ErrorType renders the Alexa.NET enum member name (MediaServiceUnknown), never the Amazon wire vocabulary (MEDIA_ERROR_UNKNOWN) that Amazon's error tables use, so log-to-docs correlation during triage requires a mental mapping; logging the wire form (or both) would make the lines self-correlating. Both live in the same files/fix as the dead-arm switch.
<!-- SECTION:NOTES:END -->

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
