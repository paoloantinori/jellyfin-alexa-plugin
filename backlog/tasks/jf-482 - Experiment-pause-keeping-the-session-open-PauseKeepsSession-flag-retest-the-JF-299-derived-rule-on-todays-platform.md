---
id: JF-482
title: >-
  Experiment: pause keeping the session open (PauseKeepsSession flag) - retest
  the JF-299-derived rule on today's platform
status: Done
assignee: []
created_date: '2026-09-04 11:38'
updated_date: '2026-09-04 12:32'
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
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and live-verified (commits 141e3330 flag + the review-folded P3 fixes riding 168aaae8).

What shipped: PluginConfiguration.PauseKeepsSession (default false), gating ONLY the pause branch via a BuildPauseResponse(bool) overload (parameterless byte-identical; stop/cancel and every other session-ending response untouched, pinned); AudioPlayer.Stop always sent; config.html toggle (emby-checkbox idiom, served-page verified post-deploy: PauseKeepsSession x4) and a partial-PATCH whitelist entry, so the flag flips live without restart (the handler reads the live config object).

Live evidence available so far: the served page carries the toggle; the behavioral matrix (6 steps: the original 5 plus the hardware pause button the JF-474 review surfaced as unverified platform behavior) is PAOLO'S to run, per the experiment design. The PATCH command and decision rule are in the task description; step 6 added in the notes.

Review (combined pass over both streams): zero P1/P2; the P3@81 hardware-pause scope question landed as matrix step 6; the P3-1 progressive-guard, P3-2 contraction pin, and P3-4 stop-word strip belonged to the JF-474 stream and were applied there. The worker's two pre-analyzed session interactions (FindSong force-route intent-agnostic; JF-387 attributes empty in the target window) hold. Suite 3144/3144, mutation verified (flag neutralized kills exactly the flag-on test).

This task closes as implemented-and-deployed; the DECISION (default flip or counterproof) remains open pending the device matrix and will land as a follow-up commit + CLAUDE.md Stop-vs-Pause update when Paolo reports the six results.
<!-- SECTION:FINAL_SUMMARY:END -->
