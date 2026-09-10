---
id: JF-501
title: >-
  Video-launch announcement cut mid-sentence on fast HLS starts: speak via
  progressive response, final response directive-only
status: Done
assignee: []
created_date: '2026-09-06 08:40'
updated_date: '2026-09-10 15:15'
labels:
  - ux
  - video
  - echoshow
dependencies: []
references:
  - JF-498
  - device test 2026-09-06
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-498's device verification (2026-09-06): the spoken announcement ('Riproduco l'ultimo episodio: X') is cut mid-sentence when the video launches on the Echo Show. With playback WORKING, the HLS playlist is ready in ~0.6s and the VideoApp player takes the audio channel before the TTS finishes; movie launches (static file, longer buffering) let the announcement complete, so the cut is specific to the fast HLS route (and any future fast-start path). Fix shape: move the announcement to a progressive response (SendProgressiveResponse, the machinery already exists and is used for the 'Ricerca dei tuoi contenuti in corso...' pre-announcement) spoken DURING resolution, and return the final response with the VideoApp.Launch directive only (no OutputSpeech); the TTS then completes before the player opens. Scope: the video launch sites that both announce and launch (PlayNextUpEpisodeAsync first, then the movie/episode launch sites for consistency; check whether movies want the same treatment for uniform behavior). Purely cosmetic UX polish; no functional bug.
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
Code-review gate (2026-09-10): findings 1 (stale fire-and-forget doc contradicting the awaited caller) and 2 (failed progressive send silently drops the announce; fix = Task<bool> + re-attach on false) applied by the fix agent, plus the two imprecise timing comments and two coverage gaps (screenless+announce-ON async builder; plain-text arm escaping). Reviewer's below-threshold scope question to resolve at the DEVICE check: the RESUMING-book announce (PlayBookIntentHandler ~:284 resume branch + BuildAudiobookResumeResponse users) still rides the final response on a VideoApp launch - if the fast-HLS cut premise applies to the sliced audiobook playlist, that announce remains cuttable (the fresh-start path WAS converted). Include 'resume a book mid-play and listen for the announce' in the device checklist; if cut, it becomes a follow-up in the JF-538 family.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge d6391ac2 + deployed (active DLL md5 1972225a verified; config survived; audio smoke green, zero FTL). The launch announce now rides an AWAITED progressive VoicePlayer.Speak sent after the launch URL resolves and before the final response returns; the VideoApp.Launch response carries the directive only. 12 launch sites converted; SearchMedia PlayItem (sync HandleFuzzyMiss delegate, JF-538) and APL UserEvent taps keep the classic shape by design. Gates: /simplify (12 recording subclasses collapsed onto C# 12 primary ctors, dead Messages accessor dropped, EscapeXml reuse verified, no plumbing awkwardness), code-review high (BOTH findings applied: the stale fire-and-forget doc that would have invited a future revert of the await; the announce-loss fallback - SendProgressiveResponse now returns Task<bool> and a failed send re-attaches the announce to the final response so it can only shift vehicle, never be lost; plus the two imprecise timing comments, the null-context guard, and 3 coverage tests: send-failure fallback, screenless+announce-ON capability Tell, plain-text speak-wrap+escaping). Suites 3577/3577 net9.0 in the worktree (worker + orchestrator independent) and 3578/3578 post-merge on main; 0 warnings both TFMs. DEVICE CHECKLIST for Paolo (the only remaining verification): launch an episode/movie and confirm the announce completes BEFORE playback starts (the original cut symptom); also resume a book mid-play and listen for the announce - the resume branch still rides the final response (reviewer's scope question; if cut, it joins the JF-538 family); worst-case tail is ~2s (progressive send timeout) and a failed send falls back to the classic shape.
<!-- SECTION:FINAL_SUMMARY:END -->
