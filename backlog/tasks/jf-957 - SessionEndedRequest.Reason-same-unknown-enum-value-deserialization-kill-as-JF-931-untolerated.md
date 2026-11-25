---
id: JF-957
title: >-
  SessionEndedRequest.Reason: same unknown-enum-value deserialization kill as
  JF-931 (untolerated)
status: Done
assignee: []
created_date: '2026-11-24 18:42'
updated_date: '2026-11-25 06:51'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: /simplify on the review-round delta only (five review-driven fixes: a renderer adoption, a guarded insert, a stance pin, an assertion, a shared helper); the worker's own 4-agent /simplify covered the base 2ffe8b51; /code-review evidence complete (worker high + orchestrator medium, all findings applied) in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-25 via the merge of worktree-agent-a662637dfab3a83c7 (base 2ffe8b51 + review round 464cb872). SEAM CORRECTION (empirically probed against the real binaries, source-verified at the Alexa.NET 1.22.0 tag): the task-pinned settings-level converter shape NEVER fires for Reason (the property-level StringEnumConverter wins; and a whole-request converter never fires because RequestConverter news the instance and Populate consults neither) — the ONLY firing seam is a ContractResolver swapping the Reason property's converter after the attribute (JsonProperty.Converter). Shipped as ReasonTolerantContractResolver + ReasonTolerantConverter + a wrapped IValueProvider: known values delegate to Alexa.NET's own converter through one shared TryReadKnownEnum (byte-identical), unknown and explicit-null bucket to Reason.Error with the raw wire preserved on the UnknownWireTypeReason carrier (thread-stash drained back-to-back onto the instance, instance-keyed ConditionalWeakTable; after the review round the drain is guarded last-write-wins so no external-library change can reintroduce the death class). The missing-key stance documented and pinned (naive-path UserInitiated default, accepted because Amazon always sends the field). The handler's Reason and Error.Type args both render through the carrier renderers (the JF-930 fold early for the Error.Type line); one shared DescribeToleratedValue composes the triage format; the controller carries the JF-957 Warn block beside JF-931's. 11 tests in the JF-931 idioms incl. the premise canary and the double-set drain pin. 6345/6345 both TFMs; merged main builds clean. Gates: worker /simplify (4 agents) + /code-review high (5 findings, 3 applied, 2 refuted-by-probe) in its session; orchestrator /code-review medium (5 findings, all applied in 464cb872).
<!-- SECTION:FINAL_SUMMARY:END -->
