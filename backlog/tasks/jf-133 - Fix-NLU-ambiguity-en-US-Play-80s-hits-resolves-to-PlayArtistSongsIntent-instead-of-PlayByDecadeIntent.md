---
id: JF-133
title: >-
  Fix NLU ambiguity: en-US "Play 80s hits" resolves to PlayArtistSongsIntent
  instead of PlayByDecadeIntent
status: Done
assignee: []
created_date: '2026-05-12 09:41'
updated_date: '2026-05-12 11:33'
labels:
  - nlu
  - en-US
  - bug
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The utterance "Play 80s hits" (en-US) resolves to PlayArtistSongsIntent instead of PlayByDecadeIntent. The word "hits" likely matches generic song/artist patterns.

The interaction model needs better disambiguation for decade-based utterances. Consider adding more decade-specific sample utterances or adjusting PlayArtistSongsIntent to avoid matching decade patterns.

NLU test fixture: en-US - "Play 80s hits" (expected PlayByDecadeIntent, got PlayArtistSongsIntent)
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
- [ ] #2 dotnet build passes with 0 errors
- [ ] #3 dotnet test passes
- [ ] #4 No new compiler warnings introduced
- [ ] #5 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #6 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #7 NLU test fixtures updated if interaction model changed
- [ ] #8 E2E test added for new intent or handler logic
- [ ] #9 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed by adding 14 new sample utterances to PlayByDecadeIntent with "hits" patterns (decade hits, greatest hits, etc.). This strengthens the NLU association between "hits" + decade patterns and PlayByDecadeIntent over the generic PlayArtistSongsIntent. All 1019 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
