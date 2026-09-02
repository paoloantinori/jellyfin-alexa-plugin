---
id: JF-432
title: >-
  Index services publish state non-atomically (torn old/new snapshot visible to
  lock-free readers mid-refresh)
status: Done
assignee:
  - zai
created_date: '2026-09-01 06:06'
updated_date: '2026-09-02 02:18'
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
- [x] #1 Each index publishes ONE immutable snapshot object (artists + topParentMap + phoneticCodes; the song equivalents) assigned to a single volatile field, so lock-free readers can never observe a torn old/new mix
- [x] #2 Readers (GetArtists, Search, SearchPhonetic, TryGetPhoneticCode) read the snapshot once per operation
- [x] #3 No per-request allocation regression on the read path (snapshot reference reads only)
- [x] #4 Tests stay green; add a regression test if feasible (a reader racing a refresh sees a consistent snapshot)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02 DEPLOYED + VERIFIED (commit 0354e51 on minix; boot slower than usual because the container loads ~10 plugins, needed a 40x5s wait loop - the AlexaSkill plugin itself loaded clean at 04:15:34, config 1 user survived). Full regression matrix green through the new snapshot path: Soul Coughing plays, tier 1.5 beatles-live 17ms, song fallback 72, Koop most-tracks pick, FindSong elicitation. Both indexes loaded through the new single-snapshot publish (1149 artists / 12766 songs).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-432: lock-free readers can no longer observe a torn old/new index state mid-refresh.

WHAT CHANGED (commit 0354e51)
- ArtistIndexSnapshot + SongNgramIndexSnapshot records: each service builds all state in LoadAsync and publishes ONE immutable snapshot to a single volatile field (replacing 3 + 5 sequential volatile writes). Readers capture the snapshot once per operation; SearchBySingleTokens receives it as a parameter so the single-token fallback structurally cannot cross publishes. The JF-419.3 layer-2 choke points and the DebouncedLibraryIndexService lifecycle are untouched; Count/SongCount/NgramCount derive from the snapshot.

VERIFICATION
- 6 new tests (3 per service): the structural one-field invariant (reflection: exactly one volatile snapshot field), snapshot-swap consistency, and a 300-iteration concurrent refresh/read hammer (min-50-reads enforced by loop condition, not a timing assert; 5/5 stable runs).
- MUTATION-VERIFIED: temporary torn publishes were injected and reverted - old TopParentMap, old PhoneticTokenIndex, and old AllEntries each fail the hammer; old BigramIndex is benign (degrades to the snapshot-consistent single-token fallback). Grep: zero references to the old per-member fields.
- Suite 2832 -> 2841 (shared run with JF-431); Release 0 warnings.
- Gates: /simplify (4 agents; IReadOnlyDictionary members, try/finally on the test reset event, hammer alternates Search/SearchPhonetic); code-review high (its composition window finding - GetArtists from snapshot A + live TryGetPhoneticCode - filed as JF-448 AC#1, the one residual affecting live routing, a 1-request window).
- Residual (JF-448): the artist search CHAIN still composes two publishes; the base-class generic + freezing + the 4-copy predicate are also there.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining
- [x] #11 or findings applied/tracked)
<!-- DOD:END -->
