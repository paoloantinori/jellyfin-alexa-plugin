---
id: JF-514
title: >-
  Resume-OFFER path mints ?start= from the stream-relative AudioPlayer offset
  for transcoded items (same provenance hazard as JF-507's Critical): apply the
  provenance rule at the offer/YesIntent site
status: Done
assignee: []
created_date: '2026-09-07 09:51'
updated_date: '2026-09-07 22:14'
labels:
  - video
  - resume
  - offset-provenance
dependencies: []
references:
  - JF-507
  - JF-505
  - LaunchRequestHandler.cs
  - YesIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-507 Critical-fix worker's out-of-scope finding (2026-09-07): the resume-OFFER path has the identical offset-provenance hazard the Critical fixed on the resume-yes path. LaunchRequestHandler seeds ResumeState.OffsetMs from context.AudioPlayer.OffsetInMilliseconds (STREAM-relative for the audio-transcode variant's output timeline), and YesIntentHandler's resume-yes (line ~226) mints that value into ?start= via ResolveAudioLaunchSource when the item routes to the transcode. Result: the same silent skip-back (a stream-relative offset treated as item-absolute). Fix needs the same provenance treatment at the offer site or at YesIntent: only item-absolute sources (DeviceQueue, session PlayState, FindLastPlayedItemWithProgress) may mint ?start=; AudioPlayer-context offsets on transcoded launches restart playback at 0 (or resolve via server progress). Evidence chain: the JF-507 worker's per-fallback classification table (AudioPlayer context = stream-relative; PlayState/DeviceQueue/FindLastPlayed = item-absolute) applies unchanged. Unit pins follow the ResumeIntentAudioVariantOffsetTests pattern.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Deeper nuance from the JF-507 fix analysis (2026-09-07): restart-at-0 (the interim behavior for stream-relative AudioPlayer offsets) loses the watched position entirely on a paused-then-resumed transcode. The genuinely correct fix carries the stream BASE through the resume chain: when the plugin mints a transcode URL with ?start=B, the device's later offset O is item-absolute as B+O, and the plugin knows B (it minted the URL - the StreamTokenCodec extension point the JF-507 task names can carry the base in the token or the resume state can store it at offer time). So the full fix shape: record base at transcode-mint time, and on resume compute B+O for the new ?start=. Scope this with JF-507's shipped restart-at-0 as the interim (no silent skip-back, but position lost across pause/resume for transcode items until this lands). Also related: JF-503's hold-for-segment makes near-head seeks survive during a running encode, which pairs with base-carrying for mid-episode resume.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped: the resume-offer path rebases device-derived offsets against the recorded transcode base. ResumeState.OffsetIsStreamRelative marks the AudioPlayer-context seed; YesIntentHandler probes routing + reads the base BEFORE the resolve (the resolve writes the ledger), mints base+offset or drops to 0 when no base exists (JF-507 interim); ResolveAudioLaunchSource records the launch base at the mint chokepoint for every Movie/Episode resolve (transcode records the minted ?start= base; raw-static records 0, the invalidation); ledger = DeviceQueue.AudioTranscodeBaseMs, per-device per-item, persisted on the existing debounce, trimmed at 200 mirroring TrimItemPositionState. The resume tail adopted the probe-first shape (single resolve, no false-base write, half the codec probes). Gates: /simplify 4-agent (applied: single-probe out-overload, BaseHandler read twin, probe-first tail, ledger trim; deferrals -> JF-520), code-review high SAFE TO MERGE (3-generation rebase composition verified correct, persistence back-compat verified, no Critical/Important defects introduced; the conditional landings applied: both false-premise comments hedged, and the pre-existing UserData-seed residual - flag=false offers can still mint stream-relative ?start for transcode-routed items - confirmed with a concrete failure walk into JF-520). Tests: 9 new (ResumeConfirmationTranscodeBaseTests) incl. keying non-leak and legacy-JSON back-compat; 3458/3458 on branch, 3462/3462 on main post-merge. Merged + deployed to minix with JF-518/JF-270 in the same DLL (symbols verified in the running binary: AudioTranscodeBaseMs + the JF-518 diagnostic literal; config intact; smoke paths clean). DoD 5-8 N/A (no HttpClient/model/locale changes; unit pins stand in for E2E).
<!-- SECTION:FINAL_SUMMARY:END -->
