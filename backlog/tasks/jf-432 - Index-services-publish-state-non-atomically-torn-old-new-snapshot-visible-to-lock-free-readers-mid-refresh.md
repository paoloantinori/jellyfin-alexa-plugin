---
id: JF-432
title: >-
  Index services publish state non-atomically (torn old/new snapshot visible to
  lock-free readers mid-refresh)
status: To Do
assignee: []
created_date: '2026-09-01 06:06'
labels:
  - code-review
  - correctness
  - index
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/ArtistIndexService.cs:97'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/SongNgramIndexService.cs:292'
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/DebouncedLibraryIndexService.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The JF-419.3 code-review finding 7 (CONFIRMED, pre-existing, deliberately SKIPPED with the note 'snapshot refactor deserves its own task' - this is that task, filed in the 2026-09-01 audit of untracked recommendations). Both index services publish their state as sequential non-atomic volatile writes (3 fields in ArtistIndexService, 5 in SongNgramIndexService): a reader scheduled mid-publish during a debounced refresh can observe a torn mix (new _artists with old _artistTopParentMap -> freshly added artists filtered out for that request; old bigram candidate IDs against new _allEntries -> empty results for an existing song). Volatile orders individual fields, not the group; IsReady is sticky so nothing blocks readers mid-publish. Transient and self-correcting, but the extraction was the moment to fix it and the shape is now stable (DebouncedLibraryIndexService) so the snapshot pattern lands once for both.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each index publishes ONE immutable snapshot object (artists + topParentMap + phoneticCodes; the song equivalents) assigned to a single volatile field, so lock-free readers can never observe a torn old/new mix
- [ ] #2 Readers (GetArtists, Search, SearchPhonetic, TryGetPhoneticCode) read the snapshot once per operation
- [ ] #3 No per-request allocation regression on the read path (snapshot reference reads only)
- [ ] #4 Tests stay green; add a regression test if feasible (a reader racing a refresh sees a consistent snapshot)
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
