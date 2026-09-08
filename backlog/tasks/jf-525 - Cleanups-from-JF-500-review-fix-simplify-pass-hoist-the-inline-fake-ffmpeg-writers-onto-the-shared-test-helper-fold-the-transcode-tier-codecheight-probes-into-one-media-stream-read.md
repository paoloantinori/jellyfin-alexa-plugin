---
id: JF-525
title: >-
  Cleanups from JF-500 review-fix simplify pass: hoist the inline fake-ffmpeg
  writers onto the shared test helper; fold the transcode-tier codec+height
  probes into one media-stream read
status: To Do
assignee: []
created_date: '2026-09-08 18:34'
labels:
  - tech-debt
  - tests
  - video-audio
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two small cleanups surfaced by the /simplify pass on the JF-500 review-fix round (2026-09-08), both in files JF-500 touched, both skipped in that round to keep the fix diff minimal.

1. Test-side: VideoAudioControllerTests.cs now has a hoisted fake-ffmpeg writer (WriteBlockingFakeFfmpeg, added with the transcode-slot tests), but ~10 older tests still carry inline copies of the same skeleton (write #!/bin/sh script under _tempDir, chmod with the CA3003/CA1416 pragmas, for-last-arg loop, dd segment, printf playlist). Consolidate them onto one shared writer (generalize WriteBlockingFakeFfmpeg into a writer that takes the body/actions) so the chmod/pragma boilerplate lives once.

2. Production-side: on the episode path the transcode tier reads the item's media streams twice (ResolveSourceVideoCodec and ResolveSourceVideoHeight each call ResolveSourceVideoStream, one IMediaSourceManager.GetMediaStreams DB read each) where one shared read would do. Not a regression (the pre-transcode code paid ResolveTotalMediaBitrateBps instead, so the read count is 3 before and after), but folding codec+height into one probe drops the tier to 2 reads. Keep the fail-open shapes (null manager / no stream) and the existing unit tests green.

Neither blocks JF-500.
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
