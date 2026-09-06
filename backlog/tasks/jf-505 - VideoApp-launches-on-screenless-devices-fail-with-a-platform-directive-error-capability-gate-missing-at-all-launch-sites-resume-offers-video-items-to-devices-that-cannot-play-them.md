---
id: JF-505
title: >-
  VideoApp launches on screenless devices fail with a platform directive error:
  capability gate missing at all launch sites + resume offers video items to
  devices that cannot play them
status: To Do
assignee: []
created_date: '2026-09-06 15:14'
labels:
  - video
  - device-capabilities
  - bug
dependencies: []
references:
  - corr=58826ad3
  - corr=f0240020
  - JF-498
  - Alexa/Interface/
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 Dot (screenless Echo) session: every video launch on a device without the VideoApp interface fails with the platform error 'il dispositivo di destinazione non supporta la direttiva specificata'. Two evidenced paths: PlayNextEpisodeIntent direct (17:09:17 corr=58826ad3: session hit, VideoApp routing ran, directive sent, platform rejected) and the accepted resume offer (17:12:00 corr=f0240020: 'Yes: confirming resume' for The Bear episode 'Ribs' launched VideoApp on the Dot). The plugin has an interface-capability layer (Alexa/Interface/, used for APL) but NO VideoApp gate at any of the 11 launch sites wired by JF-498 nor the Yes-resume path.

FIX: a shared capability check (context.System.Device.SupportedInterfaces contains VideoApp) at the GetVideoAppLaunchUrl choke point's callers (or a wrapper around the launch-response builders): without the interface, respond with a NEW localized string ('questo contenuto richiede un dispositivo con schermo' + 17 locales) instead of the directive. ALSO the resume-offer builder (LaunchResume) must not offer a VIDEO item on a screenless device: either skip the offer, or fall back to the user's last AUDIO item (the last-played ledger is per-user: it offered a Show-played episode to the Dot); the cross-device video offer can never be honored there. Unit tests: VideoApp interface present/absent at the choke point + the launch builders; resume-offer on screenless with last-played video vs audio. Device verification on the Dot.
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
