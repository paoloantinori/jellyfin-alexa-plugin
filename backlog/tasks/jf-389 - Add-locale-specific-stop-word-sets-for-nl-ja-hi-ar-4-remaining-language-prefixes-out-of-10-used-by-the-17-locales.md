---
id: JF-389
title: >-
  Add locale-specific stop word sets for nl/ja/hi/ar (4 remaining language
  prefixes out of 10 used by the 17 locales)
status: Done
assignee:
  - zai
created_date: '2026-08-21 11:16'
updated_date: '2026-08-29 06:19'
labels:
  - enhancement
  - i18n
  - tokenizer
  - search
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The StopWords dictionary in KeywordMatcher.cs currently covers 6 language prefixes: en, it, de, fr, es, pt. The skill supports 17 locales across 11 language prefixes (en, it, de, fr, es, pt, nl, ja, hi, ar + regional variants sharing prefixes). Four prefixes have NO locale-specific stop word set:

- nl (nl-NL): Dutch function words (de, het, een, van, in, op, etc.)
- ja (ja-JP): Japanese particles (no, wa, ga, wo, ni, de, etc.) - NOTE: the ja guard test for JF-384 ('watashi no uta' keeps 'no') must be reconsidered: 'no' IS a Japanese particle and SHOULD be stripped for ja-JP queries. The JF-384 exclusion of number/no from abbreviation canonicalization is separate and stays.
- hi (hi-IN): Hindi postpositions (ka, ki, ke, me, se, etc.)
- ar (ar-SA): Arabic prepositions/particles (fi, min, ila, etc.)

Without these sets, users in those locales get only English stop word stripping; their own language's function words pollute keyword matching (e.g. a Dutch user saying 'speel het lied van de band' keeps 'het' and 'de' as tokens, skewing keyword coverage ratios).

The ja case needs care: the existing test Tokenize_JaJP_NoParticle keeps 'no' because 'ja' has no set; once a ja set is added that includes 'no', that test must be updated (or the set must deliberately exclude 'no' if it is too ambiguous with the English word).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Add stop word sets for nl (Dutch), ja (Japanese), hi (Hindi), ar (Arabic) to the StopWords dictionary in KeywordMatcher.cs
- [x] #2 Each set should contain the most common function words (articles, prepositions, conjunctions, pronouns) for that language, following the same pattern as the existing en/it/de/fr/es/pt sets
- [x] #3 Unit tests: Tokenize with nl-NL, ja-JP, hi-IN, ar-SA inputs strips the locale-specific stop words
- [x] #4 The English stop words must still be stripped under ALL locales (existing behavior, guard test)
- [x] #5 No regression: existing 2656 tests green
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed the StopWords coverage: nl/ja/hi/ar sets (romaji/romanized + native-script forms), completing all 11 language prefixes used by the 17 locales. The ja 'no' ambiguity resolved by deliberate exclusion with the rationale documented in both the set comment and the guard test (same reasoning as the JF-383 abbreviation-map exclusion). Load-bearing invariant verified: no canonical abbreviation output is a stop word in any new set. 4 new unit tests + updated unknown-locale test; suite 2746 green.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
