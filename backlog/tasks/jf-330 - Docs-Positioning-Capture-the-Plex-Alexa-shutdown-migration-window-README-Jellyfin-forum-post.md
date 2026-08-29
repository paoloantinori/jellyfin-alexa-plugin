---
id: JF-330
title: >-
  Docs/Positioning: Capture the Plex-Alexa-shutdown migration window (README +
  Jellyfin forum post)
status: Done
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-08-29 14:43'
labels:
  - docs
  - positioning
milestone: m-11
dependencies: []
references:
  - README.md
  - >-
    https://forums.plex.tv/t/important-update-regarding-the-plex-alexa-skill/938054/1
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Time-sensitive market opening (competitor landscape research, 2026-07-12, sourced): Plex disabled its official Alexa skill effective **June 15, 2026** for "low usage / shifting priorities" (Plex forum announcement forums.plex.tv/t/.../938054 + The Register 2026-04-17 theregister.com/2026/04/17/alexa_loses_its_plex_appeal). That skill streamed a user's Plex media to the Echo — the same model as this plugin. Plex users are actively searching for a replacement right now, and a paid commercial product ("My Media for Alexa") is openly courting them.

This plugin's advantages over the surveyed alternatives: Jellyfin-native (no separate server/container to run, unlike the stalled Python jellyfin_alexa_skill last pushed Dec 2023), 17 locales, and audiobook HLS streaming with resume (most competitors are music-only). Action: write a short, honest positioning section in README (and optionally a Jellyfin forum post at forum.jellyfin.org) targeting Plex→Jellyfin migrators, stating what the plugin does, its real platform limits (no native scrubber, Stop/Next during playback claimed by default music service — set expectations honestly, don't oversell), and setup pointers. Do NOT fabricate stars/benchmarks; keep claims to verified capabilities.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 README has a concise positioning section aimed at users leaving Plex's discontinued Alexa skill, listing verified capabilities and honest platform limits
- [x] #2 Claims are limited to features that actually exist in the codebase (no fabricated comparisons/benchmarks)
- [x] #3 Platform limitations (no native seek bar, Stop/Next routing, custom-skill constraints) are stated plainly so expectations are set
- [x] #4 Optionally, a draft Jellyfin-forum post is prepared for the migration audience
- [x] #5 Sources for the Plex-shutdown claim are cited where the date is stated
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
README positioning section added between About and Features, targeting Plex Alexa skill migrants: verified capabilities (native plugin, 17 locales, audiobook seek bar, multi-turn search, phonetic matching), honest platform limitations (no scrubber, stop/next routing, default-music competition, either-or Echo Show) with FAQ pointers, and both Plex-shutdown sources cited. Jellyfin forum post draft prepared at docs/forum-post-draft-plex-migration.md (DRAFT status, not posted). All claims verified against the codebase and existing FAQ; no fabricated comparisons or benchmarks.
<!-- SECTION:FINAL_SUMMARY:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
