---
id: JF-931
title: >-
  Unknown Alexa error.type wire values kill the whole request at deserialization
  (PlaybackFailed never reaches its handler, flag/ordering side effects skipped)
status: Done
assignee: []
created_date: '2026-11-20 09:36'
updated_date: '2026-11-24 19:19'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-24 via the merge of worktree-agent-aa1363c04a4158d7d (worker commit 878d3871 + review round dc1b469e). The seam: a settings-level JsonConverter<Error> (ErrorTypeTolerantConverter) at the ONE production entry point SkillRequestDeserializer.Deserialize, called by both AlexaSkillController and SimulatorController; known values delegate to Alexa.NET's own StringEnumConverter (byte-identical mapped path), unknown values fall back to ErrorType.InternalError with the raw wire string preserved on UnknownWireTypeError ([JsonIgnore] RawWireType, DescribeWireType shared renderer), non-object payloads tolerated without null (ExceptionHandler reads Error.Type unguarded). The controller catch logs a redacted, surrogate-safe, 4000-char body excerpt (now on the OCE timeout path too, SliceBodyExcerpt pure and tested), and a Warn line surfaces tolerated unknowns for all three Error carriers. The worker's empirical correction is documented in code: all 33 ErrorType members carry EnumMember wire names (the probe that said otherwise was a script-host artifact); the kill is real only for values absent from BOTH vocabularies (MEDIA_ERROR_IO_LAG et al.). Side effects verified: JF-655 flag clear + JF-447 ordering registration run for tolerated events. 6220/6220 both TFMs, 15/15 deserializer family. Gates: worker ran /simplify (4 agents) + /code-review high (6 findings, 4 fixed) in its session; orchestrator ran Skill(code-review) high on the merged delta (5 findings: 3 applied in dc1b469e, 1 tracked-skip into JF-930's shared-formatter fold, 1 covered by the SliceBodyExcerpt extraction+tests). Follow-ups filed: JF-957 (SessionEndedRequest.Reason same kill) and the JF-930 scope note.
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
