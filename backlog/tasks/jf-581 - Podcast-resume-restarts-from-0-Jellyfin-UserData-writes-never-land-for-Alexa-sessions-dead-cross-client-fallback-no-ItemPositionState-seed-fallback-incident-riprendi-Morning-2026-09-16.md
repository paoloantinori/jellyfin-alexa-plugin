---
id: JF-581
title: >-
  Podcast resume restarts from 0: Jellyfin UserData writes never land for Alexa
  sessions (dead cross-client fallback + no ItemPositionState seed fallback);
  incident 'riprendi Morning' 2026-09-16
status: In Progress
assignee: []
created_date: '2026-09-16 17:05'
updated_date: '2026-09-16 17:38'
labels:
  - bug
  - resume
  - podcast
  - playback
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-16 18:28 (log-verified end to end, Jellyfin 12.1.0, plugin 0.12.1.0, user report "riprendi Morning" restarted from capo). CHAIN: (1) 18:25:54 PlayNextEpisodeIntent(series=morning) resolved NextUp episode 064caefb ('Morning Weekend - Un'opportunita storica...') and launched it (VideoApp static-stream routing; last-played ledger recorded). (2) 18:28:23 LaunchRequest: NativeControlsForAudio branch, no AudioPlayer token, device last-played 064caefb -> BuildDeviceLastPlayedOffer reads UserData.PlaybackPositionTicks = 0 -> resume offer offsetMs=0. (3) Yes -> AudioPlayer.Play offsetInMilliseconds=0 -> restarted from the beginning. USERDATA WRITE-LOSS EVIDENCE: at 18:35:09 PlaybackStopped saved 3641180000 ticks (ReportStopOrderedAsync -> SessionManager.OnPlaybackStopped logged 'User paolo stopped playback ... at 364118ms'), yet a direct read NOW shows PlaybackPositionTicks=0 AND PlayCount=0; i.e. NEITHER the start-driven nor the stop-driven SessionManager UserData persist ever landed for this item, and the plugin's cross-client direct-write fallback (PlaybackStoppedEventHandler ~line 213) is DEAD CODE in production because _userManager.GetUserById(user.Id) is fed the plugin identity id (AlexaSkillController parses the LWA access token GUID into user.Id at ~line 329), which is NOT a Jellyfin user id -> resolves null -> silent skip (its debug log line never appears in the live log). Contributing server-side facts (verified against 12.1 source + box): the item is a .strm podcast episode with RunTimeTicks=None (UpdatePlayState percentage branches all skip on runtime 0); Jellyfin's SessionManager runs a 5-minute idle playback watchdog that auto-stopped the session at 18:33:44 'at 0ms' (the Alexa AudioPlayer path reports Started once and NEVER reports progress, so the last-known position at watchdog time is the start offset 0); GetNowPlayingItem/RemoveNowPlayingItem semantics make any second stop's persist depend on session state. WHICH server-side step loses the write is not yet isolated (zero-runtime UpdatePlayState vs the IlPost feed plugin resetting episode state vs watchdog ordering), but the PLUGIN-SIDE DEFENSE is the same for all three. FIX DIRECTION (two prongs, both red-first): (a) RESUME SEED FALLBACK: BuildDeviceLastPlayedOffer (and the audio-only fallback seed) must fall back to the plugin's own ItemPositionState for the item when UserData reads 0 (the store held 3641180000 at 18:35:09 - it is written unconditionally by PlaybackStopped and is immune to the server-side loss); (b) STOP-SAVE SELF-VERIFICATION: after ReportStopOrderedAsync, re-read UserData via the SESSION-resolved Jellyfin user (ResolveJellyfinUser(session.UserId), the pattern LaunchRequestHandler already uses), and if realPositionTicks did not land, write it directly via UserDataManager.SaveUserData (fixing the dead cross-client block: resolve the user from the session, not from the plugin identity id; log when the fallback fires). ALSO NOTE for (b): UpdatePlayState on a zero-runtime item may legitimately refuse the position; the direct SaveUserData write bypasses UpdatePlayState entirely so it works regardless. SECONDARY (separate, same incident): the episode carries NO images at all (Items/{id}/Images returns []), so the Echo's built-in AudioPlayer screen is text-only; a plugin-side art fallback to the Series/Parent Primary image in the audioItem metadata (PlaybackLaunchBuilder GetImageUrl family) would restore artwork for image-less episodes. TERTIARY observation: the 18:25:54 VideoApp static-stream launch of a bare /Audio/{id}/stream URL apparently did not start playback (user re-invoked 2.5 min later) - relates to the known VideoApp-requires-video constraint, check whether PlayNextEpisode audio items should route AudioPlayer when NativeControlsForAudio is on but the item is a bare audio stream.
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
Implemented 2026-09-16, TDD red-first per prong, tree left uncommitted for the orchestrator gates.

PRONG A (resume seed fallback, LaunchRequestHandler.BuildDeviceLastPlayedOffer): when the session-resolved UserData read yields <= 0 ticks (including the jellyfinUser-resolve-failure case, which leaves positionTicks at 0), the offer now seeds from the plugin-owned ItemPositionState via new helper TryGetItemPositionStateTicks(itemId, context): reads Plugin.Instance.DeviceQueueManager.GetQueue(context device id).ItemPositionState[key = itemId "N"], null when no device/queue/position. Information log fires when the fallback seeds (the diagnostic for the server-side loss). The same fallback was applied to the screenless audio fallback seed (BuildScreenlessAudioFallbackOffer): when FindLastPlayedItemWithProgress declines (whole-flat UserData), TryBuildStoredPositionAudioOffer scans the device queue ItemIds tail-first and offers the first queued item holding a recorded position that is not a VideoApp-launch item (which also prevents recursion into the fallback through the shared builder). RED evidence: LaunchRequest_DeviceLastPlayed_UserDataZero_SeedsOffsetFromItemPositionState, LaunchRequest_DeviceLastPlayed_UserDataResolveFails_SeedsOffsetFromItemPositionState, LaunchRequest_ScreenlessVideoLastPlayed_UserDataLoss_OffersAudioFromItemPositionState (all failed pre-impl: offset 0 / no offer); control LaunchRequest_DeviceLastPlayed_UserDataHasTicks_KeepsUserDataPosition pins that a healthy UserData position is never overwritten.

PRONG B (stop-save self-verification, PlaybackStoppedEventHandler): the dead cross-client block is replaced. The Jellyfin user is now resolved from the SESSION id (_userManager.GetUserById(session.UserId); ResolveJellyfinUser was not reused because its localized error SkillResponse would be discarded), UserData is re-read after ReportStopOrderedAsync, and when it did not land (data null or PlaybackPositionTicks == 0) the handler writes data.PlaybackPositionTicks = realPositionTicks directly via _userDataManager.SaveUserData(..., UserDataSaveReason.PlaybackProgress), bypassing SessionManager.UpdatePlayState so the zero-runtime .strm shape writes too. The fallback fires at Information level; the displacement and queue-contradiction gates that guard the block are unchanged. The old !data.Played guard was dropped: a mid-play stop at a real position must persist regardless of the played flag. Null-data case creates UserItemData with Key = itemId "N". RED evidence: PlaybackStopped_SessionReportLost_WritesUserDataDirectlyWithSessionResolvedUser failed pre-impl (SaveUserData never called; user manager resolves ONLY the session user id, so the old plugin-identity-id lookup provably cannot reach the write); control PlaybackStopped_UserDataAlreadyPersisted_DoesNotOverwrite pins no overwrite when the report path did persist.

TESTS: new suites Jellyfin.Plugin.AlexaSkill.Tests/Handler/ResumeSeedFallbackTests.cs (4) and Handler/StoppedSaveSelfVerificationTests.cs (2). Full suite 3999/3999 passed on BOTH net9.0 and net10.0; dotnet build -c Release 0 warnings 0 errors. Test-only fixups during authoring: UserItemData lives in MediaBrowser.Controller.Entities on this SDK line; SetQueue replaces the queue record and drops LastPlayedItemId, so RecordLastPlayed must follow SetQueue in fixtures.

RISKS / NOT TRACKED HERE: the null-data branch guesses the UserItemData Key (itemId "N"); real Jellyfin keys come from item.GetUserDataKeys() and a wrong key would orphan the write, so live verification should confirm the incident item actually resumes. The VideoApp static-stream launch not starting playback and the missing episode artwork (task description SECONDARY/TERTIARY) are untouched by this change.
<!-- SECTION:NOTES:END -->
