---
id: JF-461
title: >-
  BrowseCategory slot values missing id fields outside the English locales (PR
  #15 follow-up)
status: In Progress
assignee: []
created_date: '2026-09-03 06:03'
updated_date: '2026-09-03 09:06'
labels: []
dependencies: []
references:
  - 'PR #15 (commit 135de9c8)'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-US.json
    (BrowseCategory with ids)
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-459 review-local gate (2026-09-03, reviewer d finding F2, mechanically confirmed): PR #15 added missing id fields to BrowseCategory's artists/albums/songs values in the 5 English models ("matching the pattern already used by other slot types") but no other locale got the fix. Current state (verified by scan): en-* locales have 3/7 BrowseCategory values with id; de-DE, es-ES/MX/US, fr-FR/CA, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA have 0/7; it-IT has 14/14 (generated with ids by the YAML template).

Fix: add stable id fields to the BrowseCategory values (artists/albums/songs at minimum, matching the English set) in the 11 non-English non-it-IT models. For it-IT, verify the YAML template already emits ids for all values and regenerate if any are missing (it currently reports 14/14, so likely nothing to do). Keep ids stable and semantic (the English models' existing ids are the naming convention to follow: inspect model_en-US.json BrowseCategory).

Note: this is data completeness for slot-type values; check whether anything server-side keys on those ids (grep BrowseCategory usage in C#) before choosing the id strings, so the ids are not just cosmetic.
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
