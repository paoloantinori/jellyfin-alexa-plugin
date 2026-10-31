---
id: JF-918
title: >-
  Issue #43: Sonos dual Alexa deviceIds (voice vs playback endpoint) break album
  auto-advance — queue/session aliasing for event devices
status: Done
assignee: []
created_date: '2026-10-31 06:44'
updated_date: '2026-10-31 12:04'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Upstream issue #43 (Tergin87, Sonos Era 100, fr-FR, plugin 1.0.2.1): album playback plays track 1 then stops; manual "next" advances. VERIFIED DIAGNOSIS (2026-10-31, mechanism traced link by link in code; no user configuration missed):

- The reporter's Sonos sends skill INTENTS with deviceId A (amzn1.ask.device.AMAUP6...) but AudioPlayer EVENTS (PlaybackStarted/NearlyFinished/Finished) with a DIFFERENT deviceId B (amzn1.ask.device.AMA6AG...). The quoted log lines are verbatim our own formats ([diag] device= lines, "[diag] play issued", PlaybackFinishedEventHandler's "queue exhausted, ending session to dismiss APL screen"), so the split is plugin-log-evidenced, not just claimed.
- Plugin session resolution keys the Jellyfin session by the Alexa deviceId: BaseHandler.HandleRequestAsync takes context.System.Device.DeviceID and calls GetSessionByAuthenticationToken(token, deviceId). Jellyfin (verified at tags v10.11.8 L1892 and v12.0 L2016) uses the NON-EMPTY passed deviceId for LogSessionActivity -> one session per (appName, deviceId). So intents resolve session A (populated by the album play: AlbumPlayService writes session.NowPlayingQueue), while events resolve session B, a fresh EMPTY session (created by the lookup itself).
- PlaybackNearlyFinishedEventHandler.ResolveNextItemId reads session.NowPlayingQueue: Count==0 -> null -> handler returns ResponseBuilder.Empty(), NO Enqueue directive. The JF-574 rehydration bridge (ProgressReporter.TryRehydrateSessionQueueFromDevice -> queueManager.GetQueue(context.GetDeviceId())) also keys by the event's deviceId B, so it cannot rescue. DeviceQueueManager._queues is likewise device-keyed, so repeat/shuffle/recovery state is invisible to the events too.
- The asymmetry the reporter observed (manual AMAZON.NextIntent works) is the discriminating proof: NextIntent arrives on the VOICE device A, resolves session A with the album queue, and advances.
- Config surface fully enumerated: no option unifies two Alexa deviceIds (DeviceLibraryBindings only scopes libraries per device). PostPlayBehavior=AutoPlay would NOT fix it: it would mask the album continuation with radio-similar tracks on the empty session queue.
- UNVERIFIED (label stays with the task): WHY the Sonos reports two deviceIds (likely the Sonos voice endpoint vs its playback endpoint as separate Amazon-account devices) and whether any Alexa-app setting avoids the split; public searches found no authoritative documentation. Ask the reporter for the two full deviceIds and how the Era 100 appears in the Alexa app (one device or two; speaker groups/stereo pairs; preferred-speaker settings).

FIX DIRECTION (to design in this task; reporter's own suggestion is sound): on a PlaybackStarted/NearlyFinished whose deviceId has no queue, alias the event device to the launch device that minted the current stream token (the token is a join key: RecordLastPlayed under device A names the item the token carries), then resolve the session/queue through the alias. Consider also context.System.unit as a key if available. Must not regress the multi-Echo household (each Echo is one consistent device) and must not adopt stale aliases.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A simulated two-deviceId sequence (intent on device A builds an album queue; AudioPlayer event on device B with the token minted by A's launch) enqueues the next album track instead of returning an empty response
- [x] #2 The alias resolution never adopts a device whose queue is unrelated to the current stream token (no cross-adoption between independent devices in a multi-Echo household)
- [x] #3 Normal single-device Echo playback is byte-identical on the play and event paths (no behavior change when intent and event deviceIds match)
- [x] #4 A unit test pins the Sonos shape end-to-end at the NearlyFinished handler level (empty session queue under the event device, queue present under the launch device)
- [x] #5 The GitHub issue #43 reply (or release notes, if fixed silently) states the mechanism in user terms and mentions the reporter's suggestion of associating the two deviceIds
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
IMPLEMENTED (2026-10-31, pre-review): (1) DeviceQueueManager: _streamLaunchOwners item-keyed ownership map written in RecordLaunchBase (covers ReplaceAll AND Enqueue directives, before the short-circuits), ResolveDeviceAlias 3-leg gate (foreign live ownership entry; requester queue absent-or-empty; requester ledger does not name the item), TTL 24h + soft cap 512 sweep on write, Clear() sweeps the cleared device's entries. (2) BaseHandler funnel: CanonicalizeSplitDeviceIdentity at funnel entry (token from AudioPlayerRequest.Token else context.AudioPlayer.Token), reassigns context.System.Device.DeviceID when aliased so every downstream per-device read (session lookup, JF-327 binding, GetDeviceId readers, direct DeviceID readers) coheres; LogInformation on alias adoption. (3) Tests: 7 gate-matrix facts in DeviceQueueManagerTests + 2 funnel-level facts (DeviceAliasFunnelTests, real funnel + real manager, session mock asserts resolution under the LAUNCH device and never under the event device; plus the cross-track ownership chain assertion). Suite 6134/6134 both TFMs, build 0 warnings both TFMs.

Live verification plan: minix deploy (deploy checklist), regression check via simulator single-device play (no alias log lines expected on normal traffic), alias behavior itself pinned by funnel tests (the alexa-request endpoint enforces Amazon signature verification, so a handcrafted event cannot reach the live funnel); reporter validates on the Sonos with a dev build.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-10-31 maintainer: full deviceIds NOT needed (PII concern) - the fix is id-agnostic (token->launch-device join, never string-comparing deviceIds beyond equality); reporter validation = install dev build and report behavior; log correlation, if ever needed, uses the already-published truncated tails.

2026-10-31 REVIEW OUTCOMES: /simplify 3-agent battery (reuse: context.GetDeviceId helper + internal caps + TTL-sweep pin applied; efficiency CLEAN except manager LogDebug dedup, applied; quality approved, accepted-tolerance notes filed). code-review high: 4 findings ALL APPLIED (intent-path narrowing to control-shaped requests; ledger-EMPTY gate leg replacing not-naming-item; vacuous leg-2 test rewritten; comment hyphens). Post-review: build 0 warnings both TFMs, suite 6137/6137 per TFM.

2026-10-31 SHIPPED: commit 8940aa62 on main, pushed. Deployed to minix per checklist (config survived, active DLL md5 match, simulator intent + play-path regression clean, ZERO spurious alias log lines on normal traffic). Dev build for the reporter: run 41186010950. GitHub reply drafted (unslopped, /tmp/issue43_reply_draft.md), awaiting maintainer approval to post (AC #5).

2026-10-31 CI + DEV BUILD: ci.yml on 8940aa62 completed/success (all 6 validators + build-and-test green on the runner). Dev build completed/success, artifact AlexaSkill_1.0.2.1-1793436442-dev (id 13036794418) linked in the reply draft. Remaining: maintainer approval to post the issue reply, then reporter device validation.

2026-10-31 LIVE-NETWORK VALIDATION (maintainer request): full simulator battery green on the deployed DLL (play/next/pause/resume/random/search/query all coherent); logs clean, ZERO alias lines; full E2E suite it-IT green on the live endpoint through the new funnel (full_chain, reliability, smoke, fast: all intents correct; 2 single-utterance flakes re-verified healthy via profile-nlu: PlayNextEpisodeIntent and FindSongByArtistIntent both resolve). The 69-72 non-it E2E reds (both runs, incl. en-US) are the documented recurring Amazon simulate skill-selection instability (memory: simulate_outage_per_locale): the failures are 'did not resolve to any intent' in the SIMULATION layer, while profile-nlu on the SAME utterances resolves the right intents on fr-FR and en-US, and the live fr-FR model keeps its catalog wiring (valueSupplier intact) - layers our deploy cannot reach (no model change shipped; CI validators green on unchanged models). No regression from the fix on the household network.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Split-device identity alias shipped for issue #43 (Sonos voice vs playback endpoint deviceIds). Stream-launch ownership map (RecordLaunchBase chokepoint, ReplaceAll + Enqueue) + ResolveDeviceAlias ledger-EMPTY stateless gate + funnel canonicalization restricted to control-shaped requests (AudioPlayer events, PlaybackController taps; never intents). Commit 8940aa62 on main, CI green, deployed and regression-verified on minix, dev build artifact 13036794418 for the reporter, issue reply posted and verified (issuecomment-6541778742). Issue #43 stays OPEN pending the reporter's on-device confirmation; close it when they confirm, and the fix rides the next release.
<!-- SECTION:FINAL_SUMMARY:END -->
