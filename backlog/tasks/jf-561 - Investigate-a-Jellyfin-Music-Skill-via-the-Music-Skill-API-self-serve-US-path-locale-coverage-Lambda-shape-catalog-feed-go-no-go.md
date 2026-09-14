---
id: JF-561
title: >-
  Investigate a Jellyfin Music Skill via the Music Skill API (self-serve US
  path): locale coverage, Lambda shape, catalog feed, go/no-go
status: To Do
assignee: []
created_date: '2026-09-14 15:04'
labels:
  - research
  - alexa-platform
  - designed
  - multi-session
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Research lead (claudedocs/research_echo-show-avs-protocol_2026-09-14.md, findings 4 and 6.2): the live Music Skill API docs now state "Anyone can build a music skill for public distribution in the United States" (the console offers a Music skill model), superseding the 2023-era partnership-only belief - though the same doc set still carries the older invitation wording for radio/podcast and the general overview, an unresolved internal inconsistency. A Jellyfin MUSIC skill (MSAPI: Alexa.Media.Search GetPlayableContent, Alexa.Media.Playback Initiate/Reinitiate with cross-device playbackSnapshot, PlayQueue, SetShuffle/SetLoop/SetRepeat) would get what our AudioPlayer skill cannot: native transport, queue controls, possible default-provider eligibility. INVESTIGATION ONLY, no code: (1) confirm MSAPI locale coverage (is it-IT / it-IT music domain supported for public distribution, or US-en only? check the supported-locales doc and the console's Music model locale list); (2) architecture fit: MSAPI skills are Lambda-hosted (our plugin serves HTTPS from Jellyfin; a thin Lambda proxy to the Jellyfin /alexaskill endpoints is the obvious shape but adds AWS cost/latency and moves trust); (3) catalog feed obligation (weekly metadata feed of the user library) and streaming-rights self-certification implications for a personal, non-distributed skill; (4) account-linking model compatibility with our LWA flow; (5) does an MSAPI skill fix the bare-stop routing problem (default music service competition) or inherit it; (6) write a go/no-go recommendation with effort estimate. Keep it a separate skill from the shipping AudioPlayer one (no migration risk). Related: JF-560 (SeekController experiment) is the cheap probe to run first.
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
