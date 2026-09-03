---
id: JF-459
title: >-
  PR #15's bare-album-carrier trim covered only the 5 English models; the 11
  non-English free-text locales still ship bare carriers (routing coin flip +
  cascade-only recall)
status: To Do
assignee: []
created_date: '2026-09-03 04:28'
labels:
  - interaction-model
  - i18n
  - routing
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/pull/15'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-345 review gate (2026-09-03, score 75, finding 1). PR #15 trimmed PlayAlbum's bare-carrier samples from ONLY the five English free-text models (en-US/GB/AU/CA/IN; verified via gh api repos/paoloantinori/jellyfin-alexa-pr/pulls/15/files). The other 11 free-text locales STILL ship bare album carriers (verified in the working tree: de-DE 'Spiele {album}', es 'Reproduce {album}', fr 'Lis {album}', pt-BR 'tocar {album}', nl 'speel {album}', ar, ja, hi variants). Consequence: in those 11 locales a bare album utterance is a routing coin flip between PlayAlbumIntent and PlaySongIntent (the collision class PR #15 fixed for English), while the JF-345 cascade only recovers the PlaySong-miss half. The task file's original '16 of 17 locales' framing conflated the free-text SLOT TYPE (true: 16/17 use AMAZON.MusicRecording) with the carrier trim (false: only 5 were trimmed). Decide: (a) extend the trim to the 11 locales (mirror PR #15's shape per locale: remove bare 'play {album}' style samples from PlayAlbumIntent in de/es/fr/pt/nl/ar/ja/hi models; the JF-345 cascade then owns bare album requests uniformly), requiring NLU fixture updates and cross-locale validation, or (b) leave the carriers (they do not break anything: routing coin flip plus cascade recall) and close this as documented behavior. Reviewer's lean: (a), since the collision was judged worth fixing for English and the same wrong-artist-vs-album symptoms apply.
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
