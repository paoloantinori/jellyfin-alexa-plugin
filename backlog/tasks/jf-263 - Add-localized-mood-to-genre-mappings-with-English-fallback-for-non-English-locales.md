---
id: JF-263
title: >-
  Add localized mood-to-genre mappings with English fallback for non-English
  locales
status: Done
assignee: []
created_date: '2026-06-06 13:29'
updated_date: '2026-06-06 19:28'
labels:
  - enhancement
  - handler
  - mood
  - i18n
  - it-IT
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The PlayMoodMusicIntent handler has a hardcoded English-only `MoodGenreMap`. When Italian users say "suonare musica rilassante", the mood "rilassante" doesn't match any key, so it falls back to searching for genre "rilassante" — which doesn't exist in most libraries.

The fix: when a mood doesn't match the MoodGenreMap directly, also try the locale's native mood synonyms mapped to English genres. For example:
- "rilassante" (it-IT) → maps to "relaxing" → searches for ["ambient", "acoustic", "jazz", "classical", "new age"]
- "energica" (it-IT) → maps to "energetic" → searches for ["rock", "electronic", "metal", "punk"]

Implementation approach options:
1. **Locale-specific synonym map**: Add a dictionary mapping locale → mood synonym → English key. Small, maintainable.
2. **Runtime translation**: Use the locale from the request to look up translations. More flexible but more complex.
3. **Hybrid**: Add Italian/Spanish/French/German mood synonyms directly to MoodGenreMap keys alongside English. Simplest.

Option 3 is simplest — just add entries like:
```
["rilassante"] = new[] { "ambient", "acoustic", "jazz", "classical", "new age" },
["relajante"] = new[] { "ambient", "acoustic", "jazz", "classical", "new age" },
["détendant"] = new[] { "ambient", "acoustic", "jazz", "classical", "new age" },
```

This covers all 17 locales' mood equivalents without changing the handler logic.

**Acceptance criteria:**
- Italian mood words ("rilassante", "allegra", "triste", "romantica", "energica", "da festa", "da allenamento", "mattutina", "serale", "da cena") map to the correct English genre sets
- At minimum the top 5 non-English locales covered (it-IT, de-DE, es-ES, fr-FR, pt-BR)
- English moods still work exactly as before
- Unit tests for the new mappings
- E2E test for "suonare musica rilassante" verifies actual playback (not just intent routing)
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Expanded LocalizedMoodMap from 16 to ~80 entries covering 5 locales (it-IT, de-DE, es-ES, fr-FR, pt-BR). Added 3 missing Italian compound moods (da festa, da allenamento, da cena). Added 22 unit tests covering all locales plus cross-locale edge cases. Build passes 0 errors/warnings, all 43 mood tests pass. Commit: 81dee1d.
<!-- SECTION:FINAL_SUMMARY:END -->
