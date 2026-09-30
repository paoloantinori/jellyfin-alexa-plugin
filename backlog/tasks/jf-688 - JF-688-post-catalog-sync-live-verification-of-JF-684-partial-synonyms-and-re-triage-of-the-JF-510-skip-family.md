---
id: JF-688
title: >-
  JF-688 - post-catalog-sync live verification of JF-684 partial synonyms and
  re-triage of the JF-510 skip family
status: To Do
assignee: []
created_date: '2026-09-30 20:05'
labels:
  - catalog
  - routing
  - verification
dependencies:
  - JF-684
references:
  - >-
    backlog/tasks/jf-684 -
    JF-684-catalog-musician-slot-blocks-intent-selection-for-non-catalog-values-bare-artist-names-produce-NO-intent-fuzzy-tiers-voice-unreachable.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn by the JF-684 worker (hand-created in the worker worktree per the
number reserve; max existing was JF-687).

JF-684 shipped the code (PartialNameSynonyms: bare first substantive word of multi-word
ARTIST names as a catalog synonym) with a live A/B proof on a throwaway catalog version,
but the LIVE skill keeps serving catalog version 1188 until the next catalog sync mints and
pins a new version. The changes are inert until then, and several pieces of verification
can ONLY run after that sync:

1. Trigger (or wait for) the catalog sync (weekly CatalogSyncTask, or the manual path used
   in past sessions), then confirm the live artist catalog version's Pink Floyd entry
   carries "Pink" and The Beatles entry carries "Beatles"
   (GET /v1/skills/api/custom/interactionModel/catalogs/{id}/versions/{v}/values, the
   JellyfinArtist valueSupplier pin in the it-IT model).
2. profile-nlu the JF-684 corpus on the LIVE skill: "suona la musica di pink" and
   "suona la musica di beatles" must now SELECT an intent with musician ER_SUCCESS_MATCH
   (the throwaway-version A/B proved the mechanism; this confirms the production path).
3. Re-triage the JF-510/JF-508 skip family with the enriched catalog: the e2e skips
   "suona i pink floyd", "metti una canzone dei beatles", "metti una canzone dei
   xyzzyfoo", "suona la band radiohead" were skipped because out-of-catalog names
   selected no intent or were absorbed by PlaySongIntent. With first-word synonyms live,
   some may unskip (profile-nlu first, then unskip fixtures only for cases that route
   stably; note the NLU trainer nondeterminism memory: probe batteries before verdicts).
4. Watch for regressions from the added synonyms: an artist whose first word is also a
   common word could now attract queries that previously routed elsewhere (the shared
   P!nk/Pink Floyd "pink" collision is known-accepted, JF-420 arbitration; look for NEW
   collisions, e.g. Real Artist vs Real Thing-class first words, and evaluate against the
   disambiguation flow).

Out of scope: any change to album-catalog synonyms (deliberately untouched, JF-508 steal
risk) unless step 4 surfaces new evidence.
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
