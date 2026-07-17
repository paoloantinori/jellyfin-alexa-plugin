---
id: JF-346
title: Generalize per-locale phonetic mispronunciation compensation beyond it-IT
status: To Do
assignee: []
created_date: '2026-07-16 17:05'
labels: []
dependencies: []
references:
  - 'JF-96.2 (artist catalog phonetic synonyms, it-IT)'
  - 'JF-332 (album catalog fix, it-IT)'
  - JF-337 (runtime phonetic/fuzzy fallback — different layer)
  - JF-345 (song-to-album cascade workaround)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The "sound compensation" feature — CatalogSyncTask writing locale-specific phonetic synonyms for foreign names into catalog-backed slot types, so a user who mispronounces a foreign title still matches — currently exists ONLY for it-IT, for artists (JellyfinArtist, JF-96.2) and albums (AlbumName, JF-332). The other 16 locales get no catalog phonetic compensation; they rely on free-text AMAZON slots plus runtime fuzzy/phonetic search (FuzzyMatcher Double Metaphone, SongNgramIndexService.SearchPhonetic, JF-337) — a different, handler-layer mechanism.

This task is the foundational capability: generalize the per-locale phonetic-synonym generation so any locale can produce appropriate compensation for foreign titles (e.g. German-phonetic-for-English, Japanese-phonetic-for-English), not just Italian-phonetic-for-English. The sibling AlbumName-coverage task and broader cross-language robustness depend on this.

Key distinction to preserve: catalog phonetic synonyms (NLU-layer, turn-1 one-shot routing, this task) vs runtime phonetic search (handler-layer fallback, JF-337) — both are "phonetic" but solve different problems at different layers; this task is about the catalog layer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CatalogSyncTask (or a generalized component) can generate locale-appropriate phonetic synonyms for a configurable set of source/target language pairs, not just Italian-for-English
- [ ] #2 At least one non-it-IT locale (e.g. de-DE or es-ES) demonstrates catalog slot values carrying phonetic synonyms for foreign titles, verified via SMAPI get-interaction-model
- [ ] #3 No regression to it-IT artist/album catalog population (JF-96.2 / JF-332)
- [ ] #4 A recorded decision on which locales receive catalog phonetic compensation vs. remain free-text + runtime fallback, with the cost/signal tradeoff documented
<!-- AC:END -->

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
