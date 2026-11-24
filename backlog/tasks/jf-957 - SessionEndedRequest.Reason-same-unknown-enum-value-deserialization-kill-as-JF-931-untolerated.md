---
id: JF-957
title: >-
  SessionEndedRequest.Reason: same unknown-enum-value deserialization kill as
  JF-931 (untolerated)
status: To Do
assignee: []
created_date: '2026-11-24 18:42'
labels:
  - robustness
  - alexa-wire
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-931 round (2026-11-24): SessionEndedRequest.Reason carries the IDENTICAL property-level StringEnumConverter kill mechanism JF-931 fixed for Error.Type, and is still untolerated. An unknown reason wire value (the vocabulary grew historically: EXCEEDED_MAX_REPROMPTS was itself a later addition; Amazon keeps adding reasons) dies at JsonConvert.DeserializeObject with the same "Requested value ... was not found" signature, so the session-ended request never reaches its handler and the session teardown side effects are silently skipped. Empirically reproduced through the new SkillRequestDeserializer.Deserialize seam after the JF-931 fix. Fix shape: extend the same settings-level converter approach to the Reason property's declaring type (or a converter on the enclosing request type), with the same fallback-member + raw-wire-string preservation pattern (UnknownWireType), reusing the ErrorTypeTolerantConverter structure in Jellyfin.Plugin.AlexaSkill/Alexa/Util/SkillRequestDeserializer.cs; add the controller-warning coverage for the new carrier. The JF-931 commit body and the deserializer's doc comment name this boundary explicitly.
<!-- SECTION:DESCRIPTION:END -->

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
