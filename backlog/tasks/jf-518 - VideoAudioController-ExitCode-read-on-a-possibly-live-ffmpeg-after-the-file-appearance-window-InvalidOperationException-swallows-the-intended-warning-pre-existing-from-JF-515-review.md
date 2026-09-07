---
id: JF-518
title: >-
  VideoAudioController: ExitCode read on a possibly-live ffmpeg after the
  file-appearance window; InvalidOperationException swallows the intended
  warning (pre-existing, from JF-515 review)
status: Done
assignee: []
created_date: '2026-09-07 20:19'
updated_date: '2026-09-07 21:35'
labels:
  - bug
  - video
  - hls
  - error-handling
dependencies: []
references:
  - JF-515
  - 'VideoAudioController.cs:256'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-515 code review (2026-09-07, verified against the current source, PRE-EXISTING, untouched by the stderr-aggregation diff):

In Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs around line 256 (the endpoint path that starts an ffmpeg encode and waits ~1s for the first output file to appear): when that file-appearance window expires while ffmpeg is STILL RUNNING, the code reads `ffmpegProcess.ExitCode` on a live process. `Process.ExitCode` throws `InvalidOperationException` when the process has not exited; the outer catch (line ~287) swallows it into a rethrow path, losing the intended warning and replacing it with an exception-shaped failure.

Effect: a slow-starting encode (first file appearing just after the window) surfaces as an exception instead of the designed graceful outcome, and the diagnostic warning the code meant to emit is lost.

Note the sibling JF-515 change (aggregating stderr drain) makes the failure TEXT more visible when it exists; this task is about the ExitCode read shape itself. Also record there (or here) the reviewer's PLAUSIBLE residual note: promoting ffmpeg error lines to Warning means a pathological input emitting thousands of AV_LOG_ERROR lines could flood the synchronous console sink at the DEFAULT log level; not reachable today (all encode paths use -c:a copy, no decoder), but revisit if a decode/transcode path is ever added to these endpoints.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce or prove by code reading: ffmpegProcess.ExitCode read while the process may still be running after the file-appearance window expired (identify every affected endpoint path)
- [ ] #2 Fix: read ExitCode only after confirming HasExited, or log the warning without ExitCode when still running; keep the intended warning from being swallowed by the outer catch
- [ ] #3 Unit test covering the still-running branch if feasible with the existing test harness (fake process or extracted decision helper)
- [ ] #4 Full suite green; /simplify + code-review high gates run before merge
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed and shipped: the StreamVideoAudio startup-window failure path no longer reads ExitCode on a live ffmpeg (InvalidOperationException previously swallowed into a bodyless 500); two distinct Warning diagnostics (exit-code arm byte-identical to the old message; still-running arm names the ~1s window, tied to named constants); the still-running arm now Kill()s before Dispose (bare catch matching the siblings per runtime-source analysis), closing an empirically-verified orphan-encode hazard with retry mid-write collision risk. Two endpoint-level regression tests pin WHICH arm ran via captured logs. Full audit of all 8 ExitCode sites in the controller (7 verified already safe). Gates: /simplify (Kill + constants + second test applied; extraction follow-up filed as JF-519), code-review high SAFE TO MERGE (both findings applied pre-merge: bare catch, arm-pinning log assertions), 3451/3451, merged a394d721 (branch 4143217a). DoD 6/8 N/A (no model/locale changes; endpoint tests stand in for E2E). Deploy folds into the next DLL deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
