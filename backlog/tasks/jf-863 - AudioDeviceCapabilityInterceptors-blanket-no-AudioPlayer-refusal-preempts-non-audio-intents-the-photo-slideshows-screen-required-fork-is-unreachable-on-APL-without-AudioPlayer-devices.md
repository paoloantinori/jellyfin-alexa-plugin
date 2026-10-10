---
id: JF-863
title: >-
  AudioDeviceCapabilityInterceptor's blanket no-AudioPlayer refusal preempts
  non-audio intents (the photo slideshow's screen-required fork is unreachable
  on APL-without-AudioPlayer devices)
status: To Do
assignee: []
created_date: '2026-10-10 22:39'
labels:
  - pipeline
  - capability
  - follow-up
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-11 by the orchestrator from the JF-329 completion round (its code-review's pre-existing finding, surfaced by the feature's capability fork).

AudioDeviceCapabilityInterceptor refuses ALL IntentRequests from devices lacking the AudioPlayer interface BEFORE handlers run. A device that carries Alexa.Presentation.APL but NOT AudioPlayer (an exotic but representable shape) would hear the audio refusal and never reach ShowPhotoAlbumIntent's PhotosRequireScreen fork - the interceptor's blanket rule preempts per-intent capability logic. Pre-existing behavior; the photo feature is the first intent whose primary surface is NOT audio, making the interaction real.

DECISION SHAPE (verify the device census first - if no real device carries APL-without-AudioPlayer, this is a theoretical shape and the disposition may be a documented boundary instead of code): either the interceptor exempts intents that declare no audio dependence (a per-intent opt-out), or the boundary is documented in the interceptor's doc with the photo feature named. Do not touch the interceptor without the census.
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
