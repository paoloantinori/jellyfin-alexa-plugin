---
id: JF-955
title: >-
  JF-933 class lives in 16/17 locales: MediaInfoType carrier collisions (fr has
  the exact unfixed shape, it-IT is immune via phrase-valued design)
status: Done
assignee: []
created_date: '2026-11-24 09:16'
updated_date: '2026-11-27 06:08'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED verified-benign (2026-11-27): the JF-933 collision class is fully adjudicated live and has NO remaining defect. FRESH AUDIT (this sitting, all 17 generated models): the class shrank from the 2026-11-24 '16/17 locales' reading (which described the pre-partial-fix era) to exactly three locales - de-DE ['titel'], fr-FR/fr-CA ['titre']; it-IT structurally immune (phrase values); the other 13 clean. LIVE VERDICTS on the current model (post the 2026-11-27 all-locale rebuild): de-DE benign (JF-933 closure baseline, run 20261127-064247: all three article carriers select play intents, slot filled); fr-FR benign (run 20261127-065633-jf955-fr-baseline: 'lis le titre / lis la chanson / joue la chanson' + non-famous synthetic title ALL select PlaySongIntent with the free-text song slot filled - the ideal outcome, not even a twin absorption; MediaInfo sentinels 'quel est le titre'/'quelle est la chanson' stay MediaInfoIntent); fr-CA covered by family evidence (its PlaySong sample set is a strict subset of fr-FR's, 21 of 22 rows, no unique carriers). RECOMMENDATION RETIRED: the phrase-valued MediaInfoType conversion for 16 locales is over-engineering - there is no live defect to fix, and the conversion would risk MediaInfoIntent recall across every locale for zero verified gain; the JF-946 SongTitle-anchor collision subfamily noted in this task rides the armed song catalog and the JF-977 pin rollout. DELIVERED this round: the fr-FR fixture pin for the colliding carrier (expectation live-verified by the baseline run itself, the phantom-red rule satisfied; same advisory NO_SAMPLE_MATCH class as the de siblings) and the closure evidence. gate-exempt: fixture-row-only change plus task records; no production code, templates, or models changed; validators and classify-fixtures green.
<!-- SECTION:FINAL_SUMMARY:END -->
