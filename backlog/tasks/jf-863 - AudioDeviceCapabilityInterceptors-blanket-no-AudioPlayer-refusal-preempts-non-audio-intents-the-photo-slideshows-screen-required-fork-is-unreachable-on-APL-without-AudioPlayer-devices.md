---
id: JF-863
title: >-
  AudioDeviceCapabilityInterceptor's blanket no-AudioPlayer refusal preempts
  non-audio intents (the photo slideshow's screen-required fork is unreachable
  on APL-without-AudioPlayer devices)
status: Done
assignee: []
created_date: '2026-10-10 22:39'
updated_date: '2026-10-11 06:21'
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
CENSUS RESULT + DISPOSITION (2026-10-11 08:20, orchestrator; the census the task demanded): Amazon's CURRENT APL doc (developer.amazon.com, add-visuals-and-audio-to-your-skill, scraped live) lists the APL device set as Echo Show family, Fire Tablet, Fire TV, and the character displays (Echo Dot with clock) - ALL audio-capable; no documented device carries Alexa.Presentation.APL without AudioPlayer, and this plugin's own logs concur (every device ever observed declared AudioPlayer; the JF-505 complement shape is screenless-lacks-VideoApp). DISPOSITION per the task's own recommendation: the DOCUMENTED BOUNDARY landed in the interceptor's doc remark (the gate refuses all IntentRequests, so ShowPhotoAlbum's screen-required fork is preempted on the hypothetical shape; LaunchRequest and APL UserEvents pass, pinned; revisit trigger = a user reporting the localized AudioPlayerNotSupported line on a screen device, the Warning log is the detection surface). The per-intent opt-out stays unimplemented: zero observed instances, and the JF-505/JF-564 precedent (gates only for live-verified absent shapes) cuts the same way. Simplify gate: 4 angles CLEAN (the two optional trims of the simplification round skipped per the repo's dense-evidence remark convention, both agents' own verdicts calling them defensible either way). CLOSED.
<!-- SECTION:NOTES:END -->
