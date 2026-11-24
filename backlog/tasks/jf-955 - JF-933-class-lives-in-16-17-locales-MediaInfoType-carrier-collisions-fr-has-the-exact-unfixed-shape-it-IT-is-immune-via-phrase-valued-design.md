---
id: JF-955
title: >-
  JF-933 class lives in 16/17 locales: MediaInfoType carrier collisions (fr has
  the exact unfixed shape, it-IT is immune via phrase-valued design)
status: To Do
assignee: []
created_date: '2026-11-24 09:16'
labels:
  - nlu
  - jF-933-followup
  - catalog
  - vocabulary
dependencies: []
references:
  - /tmp/nlu_collisions3.json
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the vocabulary collision audit (2026-11-24, the third static audit): the JF-933 class (carrier words colliding with custom slot type values, causing intent selection competition) is LIVE in 16 of 17 locales. The key findings:

1. fr-FR/fr-CA carry the EXACT unfixed JF-933 shape: MediaInfoType['titre'] collides with PlaySongIntent carrier 'Lis le titre {song}'. If a French reporter exists, this is the same bug verbatim.

2. de-DE is only PARTIALLY fixed: MediaInfoType no longer carries Lied/Song, but still carries 'Titel' (with synonym 'Name'), and 'Titel' is a live carrier in FindSong, PlayArtistSongs, and PlayLibrarySong.

3. it-IT is structurally immune on MediaInfoType (its values are question phrases, not single nouns). This design is a built-in mitigation worth considering as the fix pattern for the other 16 locales.

4. A NEW collision family from the JF-946 SongTitle anchors: the anchor seeds are the carrier nouns themselves (brano, canzone, titel, lied...). They now collide with sibling play-intent carriers. Since SongTitle is catalog-backed, the JF-684 selection-gating applies.

5. Per-locale HIGH-risk tables are in the audit output at /tmp/nlu_collisions3.json.

RECOMMENDED APPROACH: follow the it-IT pattern (phrase-valued MediaInfoType) for the other 16 locales. This means converting MediaInfoType values from single nouns to question phrases (e.g. en-US: 'song' becomes 'what song is this'). This closes the whole class structurally rather than chasing individual synonyms.
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
