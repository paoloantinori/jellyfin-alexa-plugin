---
id: JF-593
title: >-
  Locale sweep: 11 non-English locales speak English on the FindSong/book/skip
  families (21-26 keys each)
status: To Do
assignee: []
created_date: '2026-09-18 21:44'
labels:
  - bug
  - i18n
  - ux
milestone: Polish
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Systemic gap found by the JF-591/JF-592 audits: 11 of the 14 non-English locale files still carry entire English response-string families identical to en-US, spoken verbatim to non-English users. COMMON 21 keys in 10+ locales (the whole FindSong conversation: FindSongPromptArtist, FindSongPromptKeywords, FindSongFoundOne/FoundMultiple/FoundMultipleSingular, FindSongDisambiguatePick, FindSongInvalidPick, FindSongNoMatch, FindSongArtistNotFound, FindSongTooVague, FindSongTooManyNarrow; plus FlowCancelled, FolderNoPlayableContent, NoMediaToRestart, RecentlyPlayed, RestartingContent, ResumingBook, ResumingBookSsml, SkippedBack/Forward/ToEnd). Per-locale extras: ar-SA, hi-IN, ja-JP also carry ElicitBookName, NoContentInBook, NotFoundBook, SearchingBook (the audiobook search family; ja-JP also TrackByArtistSsml). Affected locales: de-DE, es-ES, es-MX, es-US, fr-CA, fr-FR, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA (21-26 keys each). it-IT is clean except RecentlyPlayed (1 key, same fix scope). en-AU/en-CA/en-GB/en-IN are English locales, exempt. NOTE the Amazon speechcon lists are locale-specific: any interjection used in ResumingBookSsml translations must exist in that locale's speechcon reference, else use plain text. Quality bar: user-facing speech in 11 languages; do not machine-dump translations without the coherence check in the AC. Full machine-readable inventory: /tmp/en_residue_inventory.json (regenerate with the comparison scan if lost).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Inventory baseline (the 21 common keys + per-locale extras in the task notes) is reduced to zero: no non-en locale carries an en-US-identical value with more than 12 alphabetic chars, verified by re-running the comparison scan
- [ ] #2 Every translation keeps placeholder count and order identical to the en-US template ({0}/{1}/{2}); validate_locales.py PASS
- [ ] #3 The FindSong family wording in each locale is a coherent conversation (prompt, disambiguation pick, found/not-found) reviewed by a fluent check or cross-checked against the locale's existing sibling strings for register
- [ ] #4 SSML variants keep their <break> tags and, where the locale uses a speechcon, an in-locale speechcon from that locale's Amazon speechcon reference
- [ ] #5 Suite passes on both TFMs without --no-build; no test asserts the English wording of the translated keys (StartOverIntentHandlerTests pins en-US, unaffected)
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
