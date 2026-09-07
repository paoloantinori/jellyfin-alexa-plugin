---
id: JF-519
title: >-
  VideoAudioController exit-path hygiene: extract the HasExited/ExitCode guard
  helper (3 copy-paste sites) + decide the Kill-before-Dispose asymmetry at
  StreamVideoAudio
status: Done
assignee: []
created_date: '2026-09-07 21:09'
updated_date: '2026-09-07 22:40'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped: internal static SafeExitCode(Process) helper owns the HasExited guard, replacing the three hand-rolled ternaries at the first-segment-wait warning sites (the copy-paste class that already bit once: JF-507 added the ternary while the fourth site sat unguarded - that fourth site was JF-518). The JF-518 if/else site deliberately keeps HasExited (its still-running diagnostic needs the branch), pointer comment added. AC#2 (Kill alignment) was found ALREADY SATISFIED: the JF-518 merge shipped Kill-before-Dispose at StreamVideoAudio before this task ran; recorded, no change needed. 4 tests pin the contract (exited=code, live=-1, disposed/never-started throw InvalidOperationException - types verified against the net9.0 runtime source; 12/12 flake probe on the live-process test). Gates: /simplify 4-angle combined agent (zero findings: no other HasExited ternary repo-wide, remaining ExitCode reads correctly unconverted, file-local home verified), code-review high SAFE TO MERGE (behavior equivalence at all 3 sites, process lifetime provably undisposed at every read). 3466/3466 on branch and main post-merge. Zero behavior change: deploy intentionally DEFERRED to the next functional DLL push (the running DLL differs only by dead-equivalent code; no restart imposed on the live box for a pure refactor). DoD 4-8 N/A (no DTO/HttpClient/model/locale changes; contract tests stand in for E2E).
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
