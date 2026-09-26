---
id: JF-636
title: >-
  JF-636 - Podcast playback speed by voice (0.25x steps) via server-side atempo
  on the AudioPlayer path
status: To Do
assignee: []
created_date: '2026-09-26 12:05'
labels:
  - feature
  - podcasts
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Feature request (Paolo, relayed 2026-09-26): podcast playback at increased speed with 0.25x steps (0.75/1.0/1.25/1.5/1.75/2.0).

FEASIBILITY (assessed 2026-09-26): native rate control is NOT available to custom skills (same MSAPI-only class as the scrubber; no directive on AudioPlayer/VideoApp, no UI control, 'Alexa faster' routes to Amazon players only). The viable path is SERVER-SIDE: an /alexaskill/api/audio-speed/{id}/{rate} HLS endpoint serving the episode with ffmpeg atempo (pitch-preserving time-stretch; 0.5-2.0 per instance, chainable to 0.25x floor), on the AUDIOPLAYER PATH ONLY (a re-launch there is normal queue behavior; on the VideoApp seek path any re-launch interaction kills the stream per the JF-635 consolidated model). Speed changes = re-launch from the rate-adjusted offset; resume math must carry the rate through the whole position chain (tracker, UserData, launch bases: stream offset x rate = content position). Voice: a SetPlaybackSpeed intent ('a velocità uno e mezzo', 'più veloce', 'più lentamente' cycling steps) in 17 locales; on AudioPlayer the one-shot re-launch is the natural entry.

Scope: new endpoint (mirror the JF-507 episode audio HLS shape), the intent + slot (steps as a custom slot type), rate-aware resume chain, per-speed cache policy (start with current-speed-only), tests + NLU fixtures. Consider a per-user default speed (podcast listeners usually want a standing preference).
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
