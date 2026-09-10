---
id: JF-534
title: >-
  Transcode-tier cache reserve (3072MB/h) exceeds the default
  VideoAudioCacheSizeMB (2048MB): E2 cache dir vanished post-play; decide cap
  default vs budget clamp + find what deleted it
status: To Do
assignee: []
created_date: '2026-09-10 06:38'
labels:
  - bug
  - cache
  - video
  - hls
  - jf500-followup
dependencies: []
references:
  - JF-500
  - JF-428
  - corr=c0c21c6a
  - 'Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs:231'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:2497'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-531 device session (2026-09-09, corr=c0c21c6a), secondary observation: the Adolescence E2 transcode cache dir was ABSENT ~10 min post-play (0 segments on disk). Config facts on main: PluginConfiguration.VideoAudioCacheSizeMB defaults to 2048 MB, while EstimateEpisodeTranscodeEncodeBytes (JF-500) reserves 3072 MB/h - a 51-min HEVC episode reserves ~2.6 GB, EXCEEDING the whole default cap before any other content counts. The JF-428 pre-encode budget pins the in-flight entry (refcounted) so in principle the sweep cannot delete an encoding dir, yet the observed dir is gone with 0 segments. Candidate explanations to distinguish: (a) a LATER sweep for a subsequent play evicted the completed E2 cache (fair eviction of completed content, cap simply too small for the tier); (b) post-completion validation failed (segment count < chapters check) and deletion followed; (c) the JF-500 monitor kill + cleanup raced; (d) the pin did not actually protect (regression in the JF-428 pin path under reserve > cap). Decide and implement: either raise the default cap to cover a typical transcode-tier episode (e.g. 4096 MB, remembering disk usage is user-visible) or make the budget path WARN + clamp sensibly when the reserve exceeds the configured cap, and fix whichever mechanism actually deleted the dir (reproduce locally with a small cap + a fake encode). Keep JF-428's invariant intact: an in-flight encode's dir is never evicted.
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
