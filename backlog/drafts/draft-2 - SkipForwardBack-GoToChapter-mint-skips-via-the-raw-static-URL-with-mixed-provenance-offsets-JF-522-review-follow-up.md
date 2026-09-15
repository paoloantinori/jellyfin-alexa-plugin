---
id: DRAFT-2
title: >-
  SkipForwardBack/GoToChapter mint skips via the raw static URL with
  mixed-provenance offsets (JF-522 review follow-up)
status: Draft
assignee: []
created_date: '2026-09-15 01:55'
labels:
  - tech-debt
  - follow-up
  - transcoding
dependencies: []
references:
  - JF-522
  - JF-507
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-522 code review (2026-09-15): SkipForwardBackIntentHandler resolves its current position as `session.PlayState.PositionTicks` (item-absolute since JF-522) OR `context.AudioPlayer.OffsetInMilliseconds` (stream-relative by Amazon platform contract) into one undistinguished value, then mints the skip target via the RAW STATIC GetStreamUrl. Latently safe today only because the only items where the two timelines diverge (transcode-routed EAC3 Movie/Episode) cannot play the raw static URL at all (the JF-507 f0240020 incident class - the stream dies on device, so no wrong position is ever served). Routing the skip through BaseHandler.ResolveAudioLaunchSource/ResolveResumedAudioLaunch (the way JumpToPosition already does) would fix both the provenance mix and the unplayable-URL problem for transcode items in one change. Same family for GoToChapterIntentHandler's current-chapter math.
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
