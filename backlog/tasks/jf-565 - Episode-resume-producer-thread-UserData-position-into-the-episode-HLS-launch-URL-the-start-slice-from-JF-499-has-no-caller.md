---
id: JF-565
title: >-
  Episode resume producer: thread UserData position into the episode HLS launch
  URL (the ?start= slice from JF-499 has no caller)
status: Done
assignee: []
created_date: '2026-09-14 22:38'
updated_date: '2026-09-17 07:45'
labels:
  - episode
  - resume
  - hls
  - videoapp
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from JF-499 (2026-09-14): the episode remux endpoint now honors ?start=<ticks> on every serve path (ServeEpisodePlaylist with EXTINF-accurate slicing), but NOTHING in production mints a ?start= on the episode URL: BaseHandler.GetVideoAppLaunchUrl calls GetEpisodeVideoAudioUrl(item.Id) with no start (BaseHandler.cs:736-739), so the resume-slice machinery is dormant. Plumbing needed: (1) add the startTicks overload to GetEpisodeVideoAudioUrl (the sibling GetEpisodeAudioUrl(itemId, startTicks) at BaseHandler.cs:751-760 is the exact model); (2) thread a UserData-derived position through GetVideoAppLaunchUrl for Episode items (VideoApp has no offset, so the slice IS the resume mechanism); (3) note the W1 interplay: the tracker now deliberately skips leaf items (only Folder keys record), so EPISODE resume positions must come from Jellyfin UserData (PlayPositionTicks), not the segment tracker; (4) decide the Video-relaunch position UX: relaunching a movie via VideoApp also cannot seek, so the same ?start= slice does not apply (movies are not HLS-sliced) - scope this task to EPISODES only; (5) on-device verification: resume mid-episode on the Echo Show, seek bar must show the sliced-relative timeline (same known limitation as audiobooks). Related: JF-563 (the audiobook Resume/StartOver NativeControlsForBooks bypass - same plumbing family, book side).
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
Implemented 2026-09-17, TDD red-first (the four mint tests failed on the pre-change tree with no ?start= on the launch URL; the fresh-play pin was green as the control), tree left uncommitted for the orchestrator gates.

URL PLUMBING (PlaybackLaunchBuilder): GetEpisodeVideoAudioUrl gained the `long startTicks = 0` overload exactly mirroring the sibling GetEpisodeAudioUrl (query becomes `?start=<ticks>&token=` only when ticks > 0; the token mint is unchanged). GetVideoAppLaunchUrl gained `long startTicks = 0` and threads it ONLY on the remux/HLS tier. Scope gate is STRUCTURAL, inside the builder: (a) EPISODE-only, `item is TV.Episode`, so a remux-routed Movie ignores the position (task scope: movie resume keeps the announced-position-only shape; the remux endpoint serves movies too, so the gate is what keeps the task episode-scoped); (b) the Static route returns before the position is ever consulted (the static /Videos stream has no seek mechanism; that platform limit is why the slice exists); (c) JF-521-clamp mirror: when RunTimeTicks is known and the position reaches or exceeds it (only stale state can), the launch degrades to a fresh start (an Information line names it) instead of slicing to a zero-length playlist. The clamp is guarded on a KNOWN runtime, so the JF-581 zero-runtime .strm shape skips it.

POSITION SOURCE (UserData-first, ItemPositionState-fallback, the JF-581 pattern): extracted as the shared static `DeviceQueueManager.ResolveResumeTicks(queueManager, deviceId, itemId, userDataTicks, userDataPlayed, logger?, logLabel)` beside GetStoredPositionTicks (the store it arbitrates): UserData ticks win when positive; a PLAYED item returns 0 (completion legitimately resets UserData; the stale stored mid-listen position must not resurrect); otherwise the plugin-owned ItemPositionState seeds (with the Information diagnostic naming the JF-581 write loss). Static-with-manager-param (not instance) so a caller with no manager reference (cold plugin) resolves without a null dance; LaunchRequestHandler.BuildDeviceLastPlayedOffer was refactored onto this helper (its inline copy and the single-caller TryGetItemPositionStateTicks accessor deleted; the ResumeSeedFallbackTests suite pins the behavior unchanged) and TvNextUpService is the second consumer, so the resolution logic now lives ONCE.

PER-CALL-SITE DECISIONS (every GetVideoAppLaunchUrl caller enumerated at HEAD):
- MINT (resume is the user's ask): ContinueWatchingIntentHandler (the scan's resumeTicks, already spoken in the announce); ResumeIntentHandler fallback-4 video branch (same shape); TvNextUpService.PlayNextUpEpisodeAsync (NextUp runs EnableResumable so an in-progress episode IS the next one and the announce already said "Resuming"; position resolved via the shared helper because this is the one mint site where the JF-581 fallback arm is LIVE - the episode comes from the NextUp query, not a UserData progress scan, so UserData can genuinely read 0 while the store holds the real stop).
- NOT MINTED, no code change (fresh is the user's ask; the default 0 keeps today's behavior): PlayEpisodeIntentHandler explicit season/episode path (by-name start-over semantics; pinned by test), StartOverIntentHandler (the intent's entire point; it also clears server-side progress first), YesIntentHandler.PlayVideo (disambiguation confirm), PlayVideoIntentHandler, SearchMediaIntentHandler (both by-name plays that may launch episodes; their "Resuming X from Y" announce stays informational - honest for movies which can never slice, and the conservative reading for episodes), PlayRandomIntentHandler (random pick).
- EPISODES CANNOT REACH THE CALL SITE (no decision needed): RecommendIntentHandler (VideoApp branch is Movie-only; the query is IsPlayed=false anyway) and AplUserEventHandler (the VideoApp branch is Movie-only; tapped episodes route to the AudioPlayer path, which already resumes via GetResumeOffset).
- ContinueWatching and ResumeIntent-4 do NOT route through the store fallback: their items come FROM the UserData progress scan (FindLastPlayedItemWithProgress), so the fallback arm would be dead code there; this matches JF-581's documented reader-side asymmetry (those seeds heal via the writer-side self-verification).

TESTS (15 new): PlaybackLaunchBuilderEpisodeResumeTests (6: remux episode mints start / no-start keeps token-only URL / static route ignores / movie ignores with the episode-only scope pin / at-or-beyond-runtime clamps to fresh / strictly-inside-runtime still slices); DeviceQueueManagerTests +4 ResolveResumeTicks cases (UserData wins, Played guard, store seeds the 0-read shape, null-manager/no-store zero); handler wiring: ContinueWatching mint, ResumeIntent fallback-4 mint, TvNextUp mint + TvNextUp UserData-write-loss seed from ItemPositionState (Plugin.Instance queue swap, the ResumeSeedFallbackTests pattern); PlayEpisode explicit-S/E fresh-play pin (progress present, no start minted). Full suite 4028/4028 passed on BOTH net9.0 and net10.0 on the final tree; dotnet build -c Release 0 warnings 0 errors. VideoApp response shapes untouched (shouldEndSession stays null; no AudioPlayer-path changes).

LEFT FOR THE USER (on-device verification, the task's own item 5): resume mid-episode on the Echo Show via "continue watching" / "next episode" with an EAC3-family episode, and confirm the seek bar shows the sliced-relative timeline (the same known limitation as audiobooks: the resume clock is relative to the resume point, not the episode absolute timeline - unavoidable, #EXT-X-START is ignored by the Echo). Also worth a live look: a Static-routed (h264+aac) episode still restarts from 0 on a resume ask (platform limit, announce over-claims there), and the NextUp Information seed line fires when the server drops a UserData write.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DONE (commit d7a4f1ac). The dormant JF-499 ?start= slicing machinery has its first production caller. Position source: DeviceQueueManager.ResolveResumeTicks (static, beside GetStoredPositionTicks, the store it arbitrates) implements the JF-581 arbitration as one shared helper - UserData wins when positive, a PLAYED item returns 0 (completion legitimately resets UserData; stale stored positions must not resurrect), otherwise the plugin-owned ItemPositionState seeds with the Information diagnostic naming the write loss. LaunchRequestHandler.BuildDeviceLastPlayedOffer was refactored onto it (inline copy + single-caller helper deleted; ResumeSeedFallbackTests pins unchanged) and TvNextUpService became the second consumer (the site where the fallback arm is LIVE: the episode comes from the NextUp query so UserData can genuinely read 0 while the store holds the stop). URL plumbing: GetEpisodeVideoAudioUrl(itemId, startTicks) mirroring the GetEpisodeAudioUrl model; GetVideoAppLaunchUrl threads it EPISODE-only (structural gate), with the Static route returning before the position is consulted and a FAIL-CLOSED runtime clamp (review minor: the original fail-open form could mint an unclamped slice serving an empty playlist on the zero-runtime .strm shape; an unknown runtime now degrades to fresh start, matching the clamp's own conservative-truth rationale). Per-site decisions over all 11 call sites: mint (resume is the ask) at ContinueWatchingIntentHandler, ResumeIntentHandler fallback-4, TvNextUpService; kept fresh (0, pinned by tests) at PlayEpisode explicit S/E, StartOver, YesIntent disambiguation, PlayVideo, SearchMedia, PlayRandom; unreachable (Movie-only branches) at Recommend and AplUserEventHandler carousel. MAJOR review finding applied: the resume announce is gated on actual DELIVERY via the new GetVideoAppLaunchUrl(item, user, startTicks, out resumeDelivered) overload - a Static-routed or clamped launch now speaks the plain now-playing shape instead of 'resuming from 12:34' the device will not honor; MOVIES keep the pre-existing informational positional announce (documented no-seek limitation). /simplify applied: token mint hoisted out of the duplicated ternary in the episode query composition. Hunt checks clean: ticks contract end to end (mint → query → [FromQuery] → EXTINF slice), no position double-count (the video remux encode takes no startTicks; the slice is per-request at serve time), shouldEndSession null throughout. Tests: 15 new (4 mint red-first, delivery-gate pins, fail-closed clamp edges, fresh-play controls, store-fallback seed with a real DeviceQueueManager), fixture updates to the fail-closed contract. Suite 4028/4028 both TFMs, Release 0 warnings. Known limits (platform): the sliced seek bar spans resume-point to end (relative clock, same as audiobooks); a Static-routed episode's resume ask plays from 0 with the plain announce; movies unchanged. On-device verification left for the user: resume mid-EAC3-episode on the Echo Show and confirm the sliced-relative timeline and the NextUp seed log line.
<!-- SECTION:FINAL_SUMMARY:END -->
