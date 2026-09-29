---
id: JF-647
title: >-
  JF-647 - speed-encode exit watcher can delete a LIVE registry entry (stale
  unconditional TryRemove after a same-key re-register): abandoned ffmpeg holds
  an encode-gate slot
status: Done
assignee: []
created_date: '2026-09-27 08:16'
updated_date: '2026-09-29 03:40'
labels:
  - playback-speed
  - encode-gate
  - race
dependencies: []
references:
  - >-
    backlog/tasks/jf-637 -
    JF-636-follow-ups-consolidate-the-variant-HLS-machinery-the-JF-632-gate-preamble-and-the-slot-resolution-walk.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-637 review round (the worker's bugs-noticed list; code is JF-636-era, moved verbatim by the JF-637 refactor).

THE RACE: RegisterLiveSpeedEncode's exit watcher (VideoAudioController.cs) polls Process.HasExited at 1s intervals and, on exit, unconditionally TryRemoves the cache key from the active-encode registry. If the OLD encode exits and a NEW same-cache-key encode registers inside that window (a user re-requesting the same speed stream), the stale watcher deletes the LIVE entry. Consequence: KillSupersededSpeedEncodes later degrades to the conservative no-kill path (the registry no longer shows the old process), so an abandoned ffmpeg runs to completion holding one of the two encode-gate slots (MaxConcurrentFfmpegEncodes=2): with two abandoned encodes the gate is fully consumed and new variant-HLS requests stall until they finish.

FIX DIRECTION: the watcher must remove ONLY the entry it registered: key the registry by a generation/token (e.g. store the Process reference and TryRemove only when the stored process is still THIS process, the standard compare-and-remove), instead of unconditional key removal.

VERIFICATION: a unit/integration test that simulates old-exit + new-register interleaving and asserts the new entry survives; the existing superseded-encode tests stay green.
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
DONE 2026-09-29 (worktree agent-a573f191777c95bf0, not pushed).

GUARD SHAPE: compare-and-remove via ConcurrentDictionary's public TryRemove(KeyValuePair) overload (.NET 5+, both TFMs). The registry already stores the Process (value type `(Process Process, string? OwnerDeviceId)`), and each registration starts a distinct Process instance that does NOT override Equals, so the Process reference IS the generation token; the watcher removes `new KeyValuePair<string, (Process, string? OwnerDeviceId)>(cacheKey, (ffmpegProcess, ownerDeviceId))`, the exact tuple it registered, and the overload's default-equality compare makes a stale watcher's removal a no-op after a same-key re-register while the own-exit removal still clears. Applied at BOTH removal sites: the exit watcher (RegisterLiveSpeedEncode) and the supersede kill (KillSupersededSpeedEncodes, `TryRemove(entry)` on the enumerated snapshot; a swap between enumeration and removal no longer deletes a newer registration, while the kill still targets the observed process). Same idiom already ships at 5 in-repo sites (VideoAudioCache, NextTrackPrecomputeCache, SessionReferenceCache). Test seams per house convention: RegisterLiveSpeedEncode private -> internal (doc names the seam), new read-only probe LiveSpeedEncodeProcessForTest.

THE PIN: RegisterLiveSpeedEncode_OldWatcherWake_DoesNotDeleteReRegisteredLiveEntry (VideoAudioControllerTests) drives the exact interleaving: old /bin/sh sleep process registers (its watcher sleeps out the 1s poll), old is killed and WaitForExit'd, new same-key process registers (registry now names the LIVE new process), then two Assert.Same checks at +1300ms/+2600ms (past the stale watcher's wake; the second covers a slow-scheduled wake so a regression cannot slip a vacuous green), then the new process is killed and the own watcher's cleanup is asserted via WaitUntilAsync. RED-GREEN PROOF: with the unconditional TryRemove temporarily restored, the pin fails (Assert.Same got null, the stale watcher deleted the live entry); with the fix it passes on both TFMs. The pin drives the registration seam directly because the endpoint cannot produce a same-key re-register without deleting a live encode's cache directory mid-flight (GetCachedHlsPlaylist serves any non-empty playlist, so the second same-key request only re-encodes after an eviction).

JF-655 NONCE EVALUATION: ORTHOGONAL, not composed. The launch-generation nonce is minted into Alexa STREAM tokens (StreamTokenCodec/PlaybackLaunchBuilder) so a same-item re-launch's late terminal events classify as displacement in PlaybackReportOrdering; it lives in the per-device Alexa-event reporting layer. The registry race is process-scoped (which OS process a server-side registry entry names) with no Alexa event anywhere in the loop; two same-key encodes can even be minted by requests carrying the same or no launch generation (a device re-request after a mid-encode cache eviction is the SAME launch). The Process reference already provides exact per-registration identity server-side, so borrowing the nonce would thread an Alexa-layer concept into the controller for zero added correctness.

VERIFICATION TAIL: Release build 0 warnings 0 errors (TreatWarningsAsErrors on); targeted pin + existing supersede test green both TFMs; FULL suite on the final tree 4719/4719 net9.0 + 4719/4719 net10.0 (4718 baseline + the 1 new pin), exit 0; existing superseded-encode tests unmodified and green.

GATES: /simplify (3 parallel angles, all no-blocking; applied their optional consistency items: the repo's KeyValuePair constructor style instead of KeyValuePair.Create, the shared WaitUntilAsync helper instead of an inline deadline loop, and a poll-cycle comment wording fix). code-review high (4 findings: the logger-capture retention fix APPLIED; the registration-side findings and the pin-determinism seam idea FILED same-turn as JF-665).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The speed-encode exit watcher and the supersede kill now remove only the registry entry they observed (compare-and-remove on the stored Process, the generation token), so a stale watcher can no longer delete a LIVE same-key entry after a mid-encode eviction plus re-request, which is what degraded the supersede kill to the conservative no-kill and let abandoned ffmpeg encodes run to completion holding encode-gate slots. New interleaving pin red-green proven; suites 4719/4719 both TFMs; the JF-655 launch nonce evaluated and left out (orthogonal layer); registration-side follow-ups filed as JF-665.
<!-- SECTION:FINAL_SUMMARY:END -->
