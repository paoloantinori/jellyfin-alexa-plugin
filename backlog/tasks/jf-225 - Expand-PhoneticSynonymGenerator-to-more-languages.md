---
id: JF-225
title: Expand PhoneticSynonymGenerator to more languages
status: Done
assignee: []
created_date: '2026-05-29 15:10'
updated_date: '2026-05-29 16:24'
labels:
  - enhancement
  - i18n
  - phonetic
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Currently covers FR, DE, IT, ES. Add phonetic synonym generators for: PT (Portuguese), JA (Japanese romaji), NL (Dutch), and potentially SV (Swedish) and PL (Polish). Each generator follows the existing pattern in Alexa/PhoneticSynonyms/ — rule-based phonetic transforms tailored to how speakers of that language mispronounce foreign artist names. Research which locales have the most users to prioritize.
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
Added phonetic synonym generators for 3 new locales, expanding coverage from 4 to 7 languages.

**New generators:**
- `PortuguesePhoneticSynonyms.cs` — pt-BR/pt-PT: th→d, ph→f, sh→ch, tion→sion, ck→k, silent h, w→u/v, article "os"
- `JapanesePhoneticSynonyms.cs` — ja-JP: th→s, ph→f, l→r, v→b, si→shi, ti→chi, tu→tsu, hu→fu, word-final vowel epenthesis, no articles
- `DutchPhoneticSynonyms.cs` — nl-NL/nl-BE: sh→sj, th→t, ph→f, ck→k, article "de"

**Modified:** `PhoneticSynonymGenerator.cs` — added "pt", "ja", "nl" dispatch cases.

**Tests:** 43 new tests (14 PT + 15 JA + 14 Dutch) plus 6 updated dispatch tests in PhoneticSynonymGeneratorTests. All 2036 tests pass.

Coverage now: IT, DE, ES, FR, PT, JA, NL (7 of 7 non-English locales that need phonetic transforms).
<!-- SECTION:FINAL_SUMMARY:END -->
