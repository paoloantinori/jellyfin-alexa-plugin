---
id: JF-636
title: >-
  JF-636 - Podcast playback speed by voice (0.25x steps) via server-side atempo
  on the AudioPlayer path
status: Done
assignee: []
created_date: '2026-09-26 12:05'
updated_date: '2026-09-26 16:16'
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
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-636 complete: podcast playback speed by voice (0.25x steps: 0.75-2.0). The /alexaskill/api/audio-speed/{id}/{ratePerMille} endpoint serves atempo-stretched HLS (AAC 192k; a filter, so copy is impossible, which incidentally solves the EAC3 family) on the AudioPlayer path only; the rate rides the whole position chain (AudioLaunchSource, DeviceQueue active/pending rate maps, compose/resume math) keeping every stored value content-relative. SetPlaybackSpeedIntent + SpeedRate slot in all 17 locales (ja-JP needed the JF-513 space rule live: the third glue-class failure, filed JF-638 for the mechanical guard); faster/slower cycle; standing per-user PodcastSpeedPerMille honored by PlayPodcast; honest refusals over VideoApp and audiobooks. The review's 7 gaps applied (queue auto-advance, sleep-timer, jump/skip all continue the rate; device-scoped superseded-encode kills; registry cleanup). 111 new tests; suite 4447x2; all 17 models rebuilt live; routing verified on 4 locales via profile-nlu. Gates: the worker ran /simplify (3 agents) + code-review high in-turn; the orchestrator re-verified the merged state (4447x2) and ran a clean delta simplify pass. Deployed. Device round pending: 'chiedi a mia collezione di riprodurre il podcast [nome]' then 'chiedi a mia collezione vai più veloce'.
<!-- SECTION:FINAL_SUMMARY:END -->
