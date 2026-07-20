---
id: JF-354
title: >-
  NLU: favor direct artist invocation over PlayMoodMusic (narrow mood
  utterances) -- pioneer it-IT, then 17 locales
status: In Progress
assignee:
  - claude
created_date: '2026-07-20 06:02'
updated_date: '2026-07-20 06:09'
labels:
  - nlu
  - interaction-model
  - mood
  - artist-search
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
"musica di norah jones" misroutes to PlayMoodMusicIntent (mood="di norah jones") instead of PlayArtistSongsIntent -- the greedy AMAZON.SearchQuery mood slot + "musica {mood}" carrier captures "music by X" (CLAUDE.md anti-pattern #3). It works (handler-side artist fallback plays Norah Jones) but wastes a ~1s genre query + adds latency, and the user rarely uses mood. Fix: narrow PlayMoodMusic's utterances so they don't win the prefix competition against direct artist invocation -- favor PlayArtistSongs. Options: (a) more precise mood utterances (mood-specific carriers/keywords that don't collide with "musica di {artist}"); (b) mood only via a clarifying question. Pioneer for it-IT (edit the YAML template + regenerate), verify via profile-nlu + on-device that "musica di norah jones" -> PlayArtistSongs AND mood utterances still route to PlayMoodMusic. Then verify/roll out to the other 16 locales.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
