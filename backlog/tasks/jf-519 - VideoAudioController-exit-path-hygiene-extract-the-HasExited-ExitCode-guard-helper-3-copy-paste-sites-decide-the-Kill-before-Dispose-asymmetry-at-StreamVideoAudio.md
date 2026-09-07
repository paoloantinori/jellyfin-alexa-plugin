---
id: JF-519
title: >-
  VideoAudioController exit-path hygiene: extract the HasExited/ExitCode guard
  helper (3 copy-paste sites) + decide the Kill-before-Dispose asymmetry at
  StreamVideoAudio
status: To Do
assignee: []
created_date: '2026-09-07 21:09'
updated_date: '2026-09-07 21:35'
labels:
  - cleanup
  - video
  - hls
  - error-handling
dependencies: []
references:
  - JF-518
  - JF-507
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-518 /simplify pass (2026-09-07). Two same-file hygiene items deliberately kept out of the JF-518 minimal fix:

(1) EXTRACTION: the guarded-ternary idiom `ffmpegProcess.HasExited ? ffmpegProcess.ExitCode : -1` is hand-rolled at three sites in VideoAudioController.cs (lines ~440, ~662, ~900; the third was added by JF-507's 772f2f9e). The bug class already recurred by copy-paste once: JF-507 added the guarded ternary while the fourth site (fixed by JF-518) sat unguarded. Extract one internal static helper owning the guard and swap the three call sites. Zero behavior change; the JF-518 site keeps its if/else (it needs the distinct still-running diagnostic, the helper cannot serve it).

(2) KILL ALIGNMENT (decision needed, not necessarily a change): the sibling first-segment failure branches (lines ~441-442, ~663-664, ~901-902) run try { ffmpegProcess.Kill(); } catch {} before Dispose(); the StreamVideoAudio failure branch only Disposes, leaving a still-running ffmpeg to finish writing a cache file the endpoint just abandoned (no gate-slot leak: the JF-421 poller releases on exit either way). Either align (Kill when still running, mirroring the siblings) or decline with reasoning; record the decision in this task either way.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Internal static helper (e.g. TryGetExitCode / SafeExitCode) owning the HasExited guard, replacing the three inline ternaries (lines ~440, ~662, ~900)
- [ ] #2 Decision recorded in notes on the Kill-before-Dispose asymmetry: either align the StreamVideoAudio failure branch with the sibling HLS sites (Kill then Dispose when still running) with a test, or decline with the reasoning (gate poller releases the slot either way; encode finishes writing an abandoned cache file)
- [ ] #3 Full suite green; /simplify + code-review high gates run before merge
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Additional observation from the JF-518 code review to fold into item (2)'s decision or a separate decision (pre-existing, unchanged by JF-518): a last-poll-gap partial file can survive the Kill - ffmpeg can create the output in the <=10ms gap between the final File.Exists poll and the Kill; a killed partial >=10KB then passes GetCachedFile's size-only validity check (VideoAudioCache.cs:139-175) and would be served as a cache hit until the art key changes (DeleteStubIfPresent only removes <10KB files). Same shape at the three HLS sibling sites.
<!-- SECTION:NOTES:END -->

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
