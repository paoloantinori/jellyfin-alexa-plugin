---
id: JF-514
title: >-
  Resume-OFFER path mints ?start= from the stream-relative AudioPlayer offset
  for transcoded items (same provenance hazard as JF-507's Critical): apply the
  provenance rule at the offer/YesIntent site
status: To Do
assignee: []
created_date: '2026-09-07 09:51'
updated_date: '2026-09-07 09:53'
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
Deeper nuance from the JF-507 fix analysis (2026-09-07): restart-at-0 (the interim behavior for stream-relative AudioPlayer offsets) loses the watched position entirely on a paused-then-resumed transcode. The genuinely correct fix carries the stream BASE through the resume chain: when the plugin mints a transcode URL with ?start=B, the device's later offset O is item-absolute as B+O, and the plugin knows B (it minted the URL - the StreamTokenCodec extension point the JF-507 task names can carry the base in the token or the resume state can store it at offer time). So the full fix shape: record base at transcode-mint time, and on resume compute B+O for the new ?start=. Scope this with JF-507's shipped restart-at-0 as the interim (no silent skip-back, but position lost across pause/resume for transcode items until this lands). Also related: JF-503's hold-for-segment makes near-head seeks survive during a running encode, which pairs with base-carrying for mid-episode resume.
<!-- SECTION:NOTES:END -->
