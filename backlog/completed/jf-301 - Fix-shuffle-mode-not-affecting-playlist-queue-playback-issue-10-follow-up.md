---
id: JF-301
title: 'Fix shuffle mode not affecting playlist/queue playback (issue #10 follow-up)'
status: Done
assignee: []
created_date: '2026-07-02 14:02'
updated_date: '2026-07-02 20:21'
labels:
  - bug
  - playback
  - shuffle
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10'
  - docs/superpowers/plans/2026-07-02-playlist-shuffle.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After v0.9.1.0 fixed the original "playlist always empty" bug (issue #10), reporter RUBIKOF confirmed that fix works but found a new problem: enabling shuffle (AMAZON.ShuffleOnIntent, verified sent in the Alexa console) does not change playback order — the playlist continues in its original order. They also observed the ShuffleOn response carries Dialog.UpdateDynamicEntities with whole-library entities (artists/songs not in the playing playlist).

CONFIRMED by code reading:
- ShuffleOnIntentHandler only calls SessionManager.OnPlaybackProgress(PlaybackOrder.Shuffle) and returns Empty(). It never writes the plugin's own authoritative per-device queue state.
- ResolveNextItemId reads session.PlayState.PlaybackOrder (Jellyfin's), NOT DeviceQueueManager.PlaybackOrder (the persisted plugin state that already exists with a SetPlaybackOrder() method but is dead in the next-item path). Two sources of truth, only one wired to playback.
- Zero test coverage of ShuffleOn/ShuffleOff handlers. GaplessPlaybackTests sets the flag directly on the mock — proving the resolve branch works GIVEN the flag, but no test exercises the handler setting it.
- One open question (the diagnostic gate in the plan): whether AMAZON.ShuffleOnIntent is even routed to the skill during AudioPlayer playback. If not, the fix is Alexa.Media.PlayQueue SetShuffle (archived JF-218) instead.

Full root-cause analysis, file-by-file changes, exact code, TDD steps, and an E2E/verification matrix are in the implementation plan. Start with the Task 1 diagnostic gate before applying the fix tasks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 During playlist/album/artist playback, AMAZON.ShuffleOnIntent causes subsequent tracks to play in a different (shuffled) order, verified on-device over >=3 transitions
- [x] #2 AMAZON.ShuffleOffIntent restores the original queue order
- [x] #3 The ShuffleOn response does NOT contain a Dialog.UpdateDynamicEntities directive with whole-library entities (artists/songs outside the current playlist)
- [x] #4 ShuffleOnIntentHandler and ShuffleOffIntentHandler each have unit-test coverage (currently 0%)
- [x] #5 DeviceQueueManager is the authoritative source of shuffle state; ResolveNextItemId reads it (with session.PlayState as fallback)
- [x] #6 Full unit suite passes with 0 new warnings (dotnet build -warnaserror)
- [x] #7 Task 1 diagnostic gate completed and its verdict (handler-entered vs not-routed) recorded in task notes before the fix tasks were applied
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed and shipped in v0.9.2.0 (commits 29a1655 + 9187666, branch fix/playlist-shuffle merged to main). ROOT CAUSE: shuffle state diverged — ShuffleOn/Off wrote Jellyfin's session.PlayState.PlaybackOrder while ResolveNextItemId read DeviceQueueManager, so the flag never affected playback; ShuffleOn also emitted Dialog.UpdateDynamicEntities with whole-library entities. FIX: DeviceQueueManager is now the authoritative shuffle store (ShuffleRemaining/RestoreOrder physically reorder the remaining queue and snapshot original order in OriginalItemIds), mirrored into session.NowPlayingQueue via MirrorQueueToSession; resolver reads it via read-only GetQueue (with PlayState fallback only when no device queue exists); the legacy random-pick branch is gated on !reshuffledQueue to prevent double-shuffle; dynamic-entities refresh is skipped on built-in playback-control intents. Review caught 3 bugs pre-ship (double-shuffle conflict, ToDictionary dup-key crash on repeated tracks, stale CurrentIndex after shuffle-off -> added MoveTo resync). ON-DEVICE VERIFIED 2026-07-02 (session 76e19023): ShuffleOn routed mid-playback and reshuffled the tail; ShuffleOff restored original order; MoveTo resync fired on real hardware. DIAGNOSTIC GATE VERDICT: AMAZON.ShuffleOnIntent DOES reach the skill during AudioPlayer playback, so the Alexa.Media.PlayQueue/JF-218 route was NOT needed. Tests: handler unit coverage added; 2458/2458 pass, 0 warnings. Design recorded in memory shuffle_authoritative_device_queue.
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
