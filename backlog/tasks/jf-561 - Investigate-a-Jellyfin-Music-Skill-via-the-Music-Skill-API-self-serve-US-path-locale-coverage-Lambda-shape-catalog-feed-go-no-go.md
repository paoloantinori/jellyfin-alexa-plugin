---
id: JF-561
title: >-
  Investigate a Jellyfin Music Skill via the Music Skill API (self-serve US
  path): locale coverage, Lambda shape, catalog feed, go/no-go
status: Done
assignee: []
created_date: '2026-09-14 15:04'
updated_date: '2026-09-19 14:08'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Research complete; report saved to claudedocs/research_alexa-music-skill-api_2026-09-19.md (4 parallel agents, 300+ live primary-source fetches, Wayback history to 2019). RECOMMENDATION: NO-GO, today. The decisive blocker: MSAPI interfaces support ONLY en-US and es-US (device-interfaces table, live Aug 2026; troubleshooting doc: 'Currently, music skills support only US English') - an it-IT household cannot use a self-serve MSAPI skill at all; the 15-locale list incl. it-IT in the API surface is invited-partner territory (the suspected doc inconsistency is real and now pinned with both quotes). Confirmed along the way: music MSAPI IS self-serve for US public distribution (the 2026-09-14 observation holds; partnership-only belief outdated for music-in-the-US, correct for radio/podcast preview and for every non-US locale); Lambda-only hosting (no HTTPS endpoint option - breaks our zero-moving-parts architecture); certification latency budgets (Initiate p99 400ms at the Lambda) structurally fight a Lambda-to-home-Jellyfin hop; our entire simulate-skill/profile-nlu tooling is unsupported for music skills; catalog obligation is 6 JSON catalog types with 500K cap and always-live dev/live sharing; the DEFAULT-PROVIDER crux (would an MSAPI skill fix our stop/next hijacking?) stays UNVERIFIED - undocumented whether a dev-stage personal skill appears in the Default Services picker, and moot behind the locale wall. Precedents: MyMedia (the successful personal-library product) is a CUSTOM AudioPlayer skill hit by exactly our hijacking - and documents that Amazon CONFIRMED the hijacking as an Amazon-side bug with a 2026Q3 fix promised (filed as JF-595: if that fix lands and covers custom skills generally, our #1 wall may be relieved and the MSAPI motivation evaporates - check bizmodeller's page after Q3 ends Sept 30); Plex's custom skill died 2026-04/06; zero open-source MSAPI implementations exist. Effort if ever green-lit (en-US parallel skill): ~4-6 weeks part-time (catalog pipeline, queue state machine, Lambda bridge, directive handlers). CLAUDE.md's stop-routing reference corrected (the 'Amazon-partnership-only' clause was outdated for music; now states self-serve-US but en-US/es-US-only). Re-evaluation triggers: (1) MSAPI locale extension, (2) the JF-595 Amazon fix, (3) a partnered path (unrealistic).
<!-- SECTION:FINAL_SUMMARY:END -->
