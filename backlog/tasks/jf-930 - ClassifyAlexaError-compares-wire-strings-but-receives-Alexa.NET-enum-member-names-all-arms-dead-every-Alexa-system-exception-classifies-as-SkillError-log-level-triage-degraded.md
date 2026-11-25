---
id: JF-930
title: >-
  ClassifyAlexaError compares wire strings but receives Alexa.NET enum member
  names: all arms dead, every Alexa system exception classifies as SkillError
  (log-level triage degraded)
status: Done
assignee: []
created_date: '2026-11-20 09:12'
updated_date: '2026-11-25 09:45'
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
gate-exempt: /simplify on the review-round delta only (six review-driven fixes: a comment rewrite, a generic builder adoption, a collision test, a severity change, a dead-code removal, a rationale correction); the worker's own 4-agent /simplify covered the base 5b2b0c1b; /code-review evidence complete (worker high + orchestrator medium, all findings applied) in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
SCOPE NOTE from the JF-931 round (2026-11-24, commit 878d3871): the tolerated-unknown rendering folds into this task's shared-formatter scope. ExceptionHandler and SessionEndedRequestHandler still render the fallback enum member (ErrorType.InternalError) for an unknown error.type wire value, and ClassifyAlexaError sees the bucket member name rather than the raw wire string; the controller's Warning line carries the raw wire value for all three Error carriers meanwhile (that part shipped with JF-931). When this task fixes the member-name-vs-wire-string comparison, also give the unknown-unknown path one shared formatter that prefers RawWireType when present.

Shipped 2026-11-25 via the merge of worktree-agent-a1f74e0d958cea82e (base 5b2b0c1b + review round 6512d3d8). ClassifyAlexaError now switches on the ErrorType ENUM (ground truth: 33 members, every one with an EnumMember wire name, NO timeout-shaped member; three of the four dead-switch strings have no enum member at all): TransientBackend/Warning covers 9 device-communication and service-transient members, everything else falls to SkillError through a discard that now fires by design; Timeout/UserError documented unreachable with a sweep test forcing a deliberate decision on any future member. The ONE shared Error formatter: UnknownWireTypeError.DescribeWireType renders the EnumMember WIRE form (via the generic BuildWireNamesByMember reflection map) for every Error-bearing log line — PlaybackFailed, SessionEnded (both args, incl. the JF-957 Reason side whose wire form arrived with this round), and ExceptionHandler, which was the last raw-enum site; tolerated unknowns keep RAW (mapped to InternalError); null payloads render UNKNOWN. Bonus hardening: an explicit "error": null payload (tolerated by the JF-931 converter) used to NRE on the classify line — now null-guards, classifies InternalError, renders UNKNOWN, and logs WARNING not Critical (a degenerate shape is not a real fault; the noise-vs-Critical fix was the review round's finding 4). Dead code removed: ErrorCategoryInfo.LocaleKey (zero production callers; class doc no longer claims locale strings). Rationale corrected: RateLimitExceeded stays TransientBackend/Warning but the arm comment says the condition self-resolves under backoff (immediate retry amplifies throttling). Collision pin: all 33 ErrorType + 3 Reason wire renderings distinct. Changed test expectations justified: five StructuredLoggingTests pins moved from member names to wire forms (the wire form is the fix's own output; log-LEVEL expectations untouched, INTERNAL_ERROR -> SkillError -> Critical holds); the removed string theory asserted the bug's own inputs. The two pre-existing CS0105 duplicate-usings in SongNgramIndexServiceTests were also removed, making the build literally 0-warning. 6360/6360 both TFMs; merged main builds clean. Gates: worker /simplify (4 agents) + /code-review high (3 applied, 1 justified) in its session; orchestrator /code-review medium (6 findings, all applied in 6512d3d8).
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
