---
id: JF-888
title: >-
  JF-891: SetPlaybackSpeed speaks CannotChangeSpeedOverVideo on the
  stale-residue shape instead of NoMediaPlaying (consumer-local corroboration,
  never a belt bound)
status: Done
assignee: []
created_date: '2026-10-15 14:57'
updated_date: '2026-10-16 08:18'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-890 altitude review (2026-10-15, call-by-call counterfactual): SetPlaybackSpeed retains a JF-890-class wrong-line shape on the stale residue (halt report + sessionless + within-window VideoApp-routed ledger entry): the resolver answers the movie item (SetPlaybackSpeedIntentHandler.cs:117), the belt answers Video, and the device hears CannotChangeSpeedOverVideo instead of the honest NoMediaPlaying (lines 132-143). Cosmetically wrong, direction safe (refuses, launches nothing). FIX MUST BE CONSUMER-LOCAL: mirror IsVideoAppMediumCorroborated at that call site or fold the corroboration into the JF-655 active-signal gate (lines 157-162); NEVER bound the belt itself (the review traced all three belt consumers regressing under a belt bound: loop falls through to the stale-token write, shuffle writes and mirrors keyed on the stale id, speed re-launches hours-old content as audio on an idle device or parallel audio over live video - the JF-655/JF-564 hazard classes). The JF-868 doctrine is the deep reason: a bare STOPPED during active VideoApp playback makes the halt report intrinsically ambiguous platform-side; the belt must keep answering who-could-own-the-screen and the discount applies only where the action launches nothing. Pin: the stale shape -> NoMediaPlaying, never the video wrong-line.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED and merged to main as 395eaf7c (JF-891: speed over stale video residue speaks the no-media tell, not the video refusal): SetPlaybackSpeed corroborates the medium consumer-locally via PlaybackLaunchBuilder.IsVideoAppMediumCorroborated before speaking the video refusal, so the stale-residue shape (aged token, no session evidence) answers NoMediaPlaying. Gates recorded in the commit: /simplify (4 agents, 2 comment trims), /code-review high (5 findings all applied), suite 5974/5974 both TFMs, red proof on the pre-fix tree. Deployed with the JF-890/892/893 staleness-family batch and build-verified on the running DLL. Accepted residual (documented at the gate): a VideoApp stream whose session was lost while it still plays lands on the no-media side, so the Stop the old refusal carried no longer reaches it; voice pause/stop (the JF-858 flat gates) remains that shape's exit. This row's record is a backfill: the execution sitting's Done transition was lost to context compaction before the record landed. gate-exempt: record backfill only, code merged as 395eaf7c with its gate record in the commit message; no new code this session.
<!-- SECTION:FINAL_SUMMARY:END -->
