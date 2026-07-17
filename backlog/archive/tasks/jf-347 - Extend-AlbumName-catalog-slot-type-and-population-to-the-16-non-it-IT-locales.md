---
id: JF-347
title: Extend AlbumName catalog slot type and population to the 16 non-it-IT locales
status: To Do
assignee: []
created_date: '2026-07-16 17:05'
updated_date: '2026-07-16 17:08'
labels: []
dependencies: []
references:
  - 'JF-332 (AlbumName catalog fix, it-IT)'
  - JF-96.2 (catalog phonetic architecture)
  - JF-345 (song-to-album cascade — workaround this task supersedes for albums)
  - 'sibling: generalize per-locale phonetic compensation feature'
  - 'JF-346 (prerequisite: per-locale phonetic compensation feature)'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Only it-IT's model defines the AlbumName custom slot type; the other 16 locales use free-text AMAZON.MusicRecording for PlayAlbumIntent.album, so CatalogSyncTask's album values are inert there (the validator flags ~16 "Missing slot type 'AlbumName'" warnings, one per locale). This is why those 16 locales lack one-shot album routing and cross-language phonetic album matching, and why the song-to-album cascade (JF-345) exists as a handler-side workaround for them.

Goal: add the AlbumName slot type to the targeted locale models and make CatalogSyncTask populate it per-locale with locale-appropriate phonetic synonyms. The meaningful value (cross-language matching) depends on the per-locale phonetic-compensation feature (sibling task); the bare slot-type addition is independent and mechanical.

Tradeoffs to weigh before doing all 16: per-locale SMAPI catalog deploys multiplied by 16, the catalog-sync path is fragile (polling bug fixed in JF-332, plus token expiry and a weekly schedule that was not firing), and locale-appropriate phonetics are required (not just the Italian-for-English set). A subset decision (only locales where cross-language matching matters most) may be preferable to all 16.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 AlbumName slot type present in each targeted locale's model (validator 'Missing AlbumName' warnings cleared for those locales)
- [ ] #2 CatalogSyncTask populates AlbumName per targeted locale with the user's real albums plus locale-appropriate phonetic synonyms, verified via SMAPI get-interaction-model
- [ ] #3 An arbitrary user-library album routes one-shot to PlayAlbumIntent in at least one newly-enabled non-it-IT locale, verified via profile-nlu AND on-device
- [ ] #4 No regression to it-IT album routing (JF-332), PlaySong, or PlayArtist
- [ ] #5 An explicit recorded decision on which of the 16 locales to enable (all vs. subset) with rationale
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
