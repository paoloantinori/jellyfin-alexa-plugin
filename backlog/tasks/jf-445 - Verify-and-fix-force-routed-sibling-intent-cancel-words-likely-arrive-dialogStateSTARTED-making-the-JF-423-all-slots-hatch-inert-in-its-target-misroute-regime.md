---
id: JF-445
title: >-
  Verify and fix: force-routed sibling-intent cancel words likely arrive
  dialogState=STARTED, making the JF-423 all-slots hatch inert in its target
  misroute regime
status: To Do
assignee: []
created_date: '2026-09-01 22:27'
labels:
  - code-review
  - dialog
  - verification
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:180'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:414'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Review finding from the JF-423 gates (2026-09-02, three angles + one verifier, evidence conflicting): the all-slots cancel hatch (AnySlotIsCancelWord) is gated on DialogState==IN_PROGRESS, but the misroute regime it was written for (JF-423 AC#5: 'annulla' resolved to a sibling intent with musician='annulla', force-routed back by AlexaSkillController:414) likely arrives with that sibling intent's just-STARTED dialog state, not IN_PROGRESS - so the conjunction may never fire on real traffic and the trap loop the AC claims closed stays open. Counter-evidence: JF-411 closure records IN_PROGRESS mid-flow branches as simulator-verified, and JF-422 records captured elicit replies arriving IN_PROGRESS (same-intent captures). The unit test fabricates IN_PROGRESS so it cannot detect this. The added log line records dialogState, making on-device confirmation cheap. NOTE the design tension to resolve in the fix: widening to STARTED risks false-cancelling multi-word searches naming real artists whose name IS a cancel word (the band 'Basta'); a bare-word guard on the slot value resolves it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Gather evidence from podman logs: the JF-423 hatch log line now records dialogState for every force-routed FindSong request during open sessions; on-device or simulate-skill, trigger a sibling-intent misroute ('annulla' resolving to PlaySongIntent during an open FindSong artist elicit) and read the actual dialogState (expected STARTED per the JF-411 on-device observation for fresh sibling resolutions)
- [ ] #2 If STARTED confirmed: extend the hatch gate to accept the force-routed shape (sessionData present + cancel word in a NON-primary slot + dialogState IN_PROGRESS-or-STARTED), keeping the Basta-band false-positive guard (a multi-word utterance naming a real artist must still search - the cancel must be a BARE cancel word, single-token slot value)
- [ ] #3 If IN_PROGRESS observed instead: close this task with the evidence, the current gate already covers it
- [ ] #4 Unit tests for whichever shape lands
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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
