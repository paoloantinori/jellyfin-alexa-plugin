---
id: JF-575
title: >-
  Verify and fix lowercase api_key URLs against the Jellyfin 12.0 auth change
  (art images, PlaybackInfo, live-TV master; streams known-safe)
status: To Do
assignee: []
created_date: '2026-09-16 08:29'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/LaunchRequestHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/LiveTvStreamResolver.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-315 batch-11 review-local gate (reviewer A surfaced it while relocating one of the sites; pre-existing, byte-identical at HEAD, NOT introduced by the batch).

CLAUDE.md's live-verified 12.0 auth note (2026-09-09, production 12.0.0 box): lowercase `api_key` query and the X-Emby-Token header are REJECTED (401) on the general API surface; working shapes are `?ApiKey=` (capital) and `Authorization: MediaBrowser Token=`.

Five production sites still build lowercase `api_key` URLs:
1. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/LaunchRequestHandler.cs:561 (GetRecentlyPlayedItems art URL, Items/{id}/Images/Primary) - welcome-screen carousel art likely 401s on 12.0 (UNVERIFIED: image endpoints were not probed in the 2026-09-09 session)
2. Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs:626 (GetImageUrl, same image shape, same unverified status)
3. Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs:616 (stream URL; KNOWN-SAFE on both lines per CLAUDE.md: /Audio+/Videos stream endpoints serve media regardless of key shape, jellyfin#13984)
4. Jellyfin.Plugin.AlexaSkill/Alexa/Util/LiveTvStreamResolver.cs:67 (PlaybackInfo + AutoOpenLiveStream; unverified on 12.0)
5. Jellyfin.Plugin.AlexaSkill/Alexa/Util/LiveTvStreamResolver.cs:118 (live-TV HLS master fallback; stream-shaped, probably in the open-stream class but unverified)

Do this: (1) probe each endpoint family on the 12.0 box (image, PlaybackInfo, live-TV master) with lowercase api_key vs ApiKey; (2) switch every site that 401s to the capital-ApiKey shape (mechanically safe: Jellyfin 10.11 accepts both shapes per the same research); (3) add a unit assertion pinning the capital shape at the URL builders. Do NOT touch the stream endpoints' behavior beyond the key casing (they work today on both lines).
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
