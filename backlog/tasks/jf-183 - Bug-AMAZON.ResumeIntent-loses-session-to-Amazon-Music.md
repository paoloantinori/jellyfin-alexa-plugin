---
id: JF-183
title: 'Bug: AMAZON.ResumeIntent loses session to Amazon Music'
status: Done
assignee: []
created_date: '2026-05-19 16:40'
updated_date: '2026-05-19 18:16'
labels:
  - bug
  - nlu
  - playback
  - it-IT
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After pausing audio from the Jellyfin skill, saying "Alexa riprendi" (resume in Italian) causes Alexa to respond "riprendo amazon music" — the utterance is routed to the built-in Amazon Music skill instead of staying in the Jellyfin skill session.

This is an NLU competition issue. The built-in AMAZON.ResumeIntent or the Amazon Music skill's utterances are matching "riprendi" with higher priority than our custom skill.

Key questions to investigate:
1. Does our skill handle AMAZON.ResumeIntent? If so, does it need better utterance samples?
2. Is the session being kept open after pause? (shouldEndSession=false is required)
3. Does this happen only in it-IT or also en-US ("resume")?
4. Is the AudioPlayer state preserved so Alexa knows the skill owns the audio stream?
5. Are there Alexa best practices for retaining session ownership during audio playback?

The fix likely involves ensuring:
- AudioPlayer directives maintain session ownership
- ResumeIntent handler properly resumes from AudioPlayer state
- Possibly adding more utterance samples for resume in it-IT
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Investigation Plan

### Phase 1: Research (sc:research)
- Search for Alexa developer documentation on session ownership during AudioPlayer playback
- Research how built-in skills compete with custom skills for resume/pause utterances
- Find best practices for AMAZON.ResumeIntent handling in custom Alexa skills
- Check if AudioPlayer.PlayDirective with shouldEndSession matters for session retention

### Phase 2: Explore (codebase)
- Find and read ResumeIntentHandler.cs — check if it handles AudioPlayer context
- Check PauseIntentHandler.cs — verify shouldEndSession=false on pause response
- Check BaseHandler.BuildAudioPlayerResponse — verify session handling on play directives
- Read it-IT interaction model (model_it-IT.json) for ResumeIntent utterance samples
- Check en-US model for comparison
- Look at how AudioPlayer.PlayDirective sets shouldEndSession

### Phase 3: Fix
- Based on findings, likely fixes:
  - Ensure AudioPlayer responses don't end the session (shouldEndSession handling)
  - Add/improve ResumeIntent utterance samples in it-IT
  - Possibly resume from AudioPlayer context token/offset rather than session state
- Update handler logic if needed
- Add/update tests
- Deploy and verify via simulator
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Root cause identified: ResumeIntentHandler uses PlayBehavior.Enqueue (line 109) which fails silently after AudioPlayer.Stop since there's no active stream. Alexa routes resume to Amazon Music as fallback. Fix: use PlayBehavior.ReplaceAll. Also need to handle PlaybackController.PlayCommandIssued for hardware button presses.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Root cause: ResumeIntentHandler used PlayBehavior.Enqueue which fails silently after AudioPlayer.Stop (no active stream to append to). Alexa routes "resume" to Amazon Music as fallback. Fixed by switching to PlayBehavior.ReplaceAll which starts a new stream regardless of queue state. Also added PlaybackController.PlayCommandIssued handling for hardware play button presses. 3 new tests (PlayBehavior verification, PlaybackController Play/Pause canHandle).
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
