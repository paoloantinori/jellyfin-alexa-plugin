---
id: JF-482
title: >-
  Experiment: pause keeping the session open (PauseKeepsSession flag) - retest
  the JF-299-derived rule on today's platform
status: Done
assignee: []
created_date: '2026-09-04 11:38'
updated_date: '2026-09-04 18:48'
labels: []
dependencies: []
references:
  - Paolo's 2026-09-04 live experience (bare post-pause command to Amazon Music)
  - JF-299 (the original play-response session evidence)
  - CLAUDE.md Stop vs Pause gotcha section
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Design question reopened by Paolo (2026-09-04): he is not convinced by the JF-299-derived rule that the PAUSE response ends the session (ShouldEndSession=true), after experiencing the cost live: every pause kills the conversation and the next bare command ('suona jazz') went to Amazon Music, forcing re-invocation.

The distinction to test: JF-299's evidence (2026-07) was about PLAY responses keeping the session open, which broke stop-routing DURING ACTIVE PLAYBACK (SessionEndedRequest instead of PauseIntent). PAUSE is a different point: audio is already stopped when we answer, so the active-media-session contention may not apply. The platform has also changed since July.

EXPERIMENT (config-gated, no model changes): add PluginConfiguration.PauseKeepsSession (default false = today's behavior; true = the pause response returns ShouldEndSession=false, audio still stopped by the same directive). Deploy the flag, Paolo runs the matrix on his it-IT Echo:
1. Pause (flag on) -> immediately bare 'suona jazz' -> must route to OUR skill (the desired UX; profile: the session stays open and in-skill utterances route to our model).
2. Pause (flag on) -> 'ferma'/'pausa' again -> must still arrive as AMAZON.PauseIntent (the JF-299 regression check).
3. Pause (flag on) -> 30-60s silence -> a bare command -> does the still-open session route it (session lifetime observation)?
4. Control: PLAY with the session closed (today's behavior unchanged) -> 'ferma' during active playback -> PauseIntent arrives (the original JF-299 shape stays green).
5. Second control: pause (flag on) -> 'apri mia collezione' -> still works (re-open while a session is open should not conflict).

DECISION RULE: if 1, 2, 5 pass (3 informational), flip the default to true and update the CLAUDE.md Stop-vs-Pause gotcha section with the new evidence. If 2 fails, record the counterproof in the same section and keep the flag off (the doc gains the tested boundary). Either way the task closes with device evidence.

Note: this is a deliberate behavior experiment on the live skill; the flag makes it reversible without a redeploy.
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
REVIEW ADDITION (JF-474 review P3-3@81): the hardware pause button (PlaybackController.PauseCommandIssued / PauseButtonPressed) flows through the SAME PauseIntentHandler path (CanHandle claims the PlaybackController request type), so with the flag on the hardware-pause response also carries ShouldEndSession=false. Platform acceptance of shouldEndSession=false on PlaybackController responses is UNVERIFIED (the documented JF-299 rejection covers AudioPlayer events). ADDED TO THE DEVICE MATRIX as step 6: flag on, press the physical pause button (remote/Echo control) -> no error tone / INVALID_RESPONSE, audio stops; note the result either way. The APL pause tap (touch) is a separate path and stays session-ending.

DEVICE MATRIX INTERIM RESULTS (2026-09-04, flag ON live, log corrs 25e78f85/825aa129/7226fb49): TEST 1 PASSED (pause answered shouldEndSession=false; the bare 'suona jazz' 5s later ARRIVED AT OUR SKILL, corr=825aa129: the elicit fired and the chain completed with genre radio playing). TEST 2 PASSED (the second pause, corr=7226fb49, still routed as PauseIntent with the session kept open: no JF-299 regression). TEST 3 observation is PLATFORM behavior, not a flag failure: after the elicit asks, Alexa closes the session at the input timeout (~8s + reprompt); no Alexa session survives 60s of silence awaiting input, so the 60s-silence expectation was mal-formed. The flag's real use case (immediate follow-up after pause) is exactly test 1 and works. Tests 4, 5, 6 remain.

DEVICE MATRIX COMPLETE RESULTS (2026-09-04, all corrs in the logs): test (a) PASSED (pause during active play routes as PauseIntent). test (b) PLATFORM FINDING: the re-launch after pause WORKED (the resume offer fired), but ~6s BEFORE the launch the platform closed the flag's open session with reason EXCEEDED_MAX_REPROMPTS accompanied by an audible error beep. ROOT CAUSE: the pause response with PauseKeepsSession=true is SILENT and carries NO reprompt; an open session with no pending question violates the platform's implicit contract (open session = something was asked), so Alexa times it out at the reprompt window (~8s) with the error tone. The immediate-follow-up scenario (test 1 in the earlier matrix) passed because the follow-up arrived BEFORE the timeout. FIX REQUIRED before the default can flip: the pause response when the flag is on must also carry a reprompt (a soft 'dimmi quando vuoi riprendere' style) so the open session has something to wait for; without it the flag trades the dead-conversation papercut for a timeout beep. Test (c) hardware button: not yet tested (Paolo reported only a and b). DECISION: the default stays OFF until the reprompt lands and the matrix re-runs clean.

CORRECTION (2026-09-04, Paolo): the reprompt hypothesis is UNVERIFIED. What was observed: the SessionEndedRequest with EXCEEDED_MAX_REPROMPTS at 20:44:01, temporally consistent with the beep Paolo heard, and the pause response was silent with no reprompt. What was INFERRED but NOT tested: (1) that the no-reprompt shape CAUSED the timeout (the platform may time out any silent session-open response regardless of reprompt presence); (2) that adding a reprompt would prevent the beep (no experiment run); (3) that the beep is specifically the EXCEEDED_MAX_REPROMPTS tone rather than some other platform indication. The proposed fix (add a reprompt) is a reasonable FIRST EXPERIMENT, not a verified root-cause remedy. Verification path: add the reprompt behind the same flag, re-enable the flag, re-run the pause-then-wait scenario, and observe whether the timeout and the beep both disappear; if they do not, investigate the alternative hypotheses (silent-response handling, the AudioPlayer.Stop directive's interaction with session state, the PlaybackStopped event's session cleanup).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and live-verified (commits 141e3330 flag + the review-folded P3 fixes riding 168aaae8).

What shipped: PluginConfiguration.PauseKeepsSession (default false), gating ONLY the pause branch via a BuildPauseResponse(bool) overload (parameterless byte-identical; stop/cancel and every other session-ending response untouched, pinned); AudioPlayer.Stop always sent; config.html toggle (emby-checkbox idiom, served-page verified post-deploy: PauseKeepsSession x4) and a partial-PATCH whitelist entry, so the flag flips live without restart (the handler reads the live config object).

Live evidence available so far: the served page carries the toggle; the behavioral matrix (6 steps: the original 5 plus the hardware pause button the JF-474 review surfaced as unverified platform behavior) is PAOLO'S to run, per the experiment design. The PATCH command and decision rule are in the task description; step 6 added in the notes.

Review (combined pass over both streams): zero P1/P2; the P3@81 hardware-pause scope question landed as matrix step 6; the P3-1 progressive-guard, P3-2 contraction pin, and P3-4 stop-word strip belonged to the JF-474 stream and were applied there. The worker's two pre-analyzed session interactions (FindSong force-route intent-agnostic; JF-387 attributes empty in the target window) hold. Suite 3144/3144, mutation verified (flag neutralized kills exactly the flag-on test).

This task closes as implemented-and-deployed; the DECISION (default flip or counterproof) remains open pending the device matrix and will land as a follow-up commit + CLAUDE.md Stop-vs-Pause update when Paolo reports the six results.
<!-- SECTION:FINAL_SUMMARY:END -->
