---
id: JF-245
title: Implement KeywordMatcher utility for partial-title tokenization and scoring
status: Done
assignee: []
created_date: '2026-06-03 19:10'
updated_date: '2026-06-03 19:42'
labels:
  - enhancement
  - search
dependencies: []
references:
  - docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Create `Alexa/Util/KeywordMatcher.cs` — a static utility that tokenizes song titles and user keyword input, then scores matches.

## Algorithm

1. **Tokenize**: lowercase, split on spaces/punctuation, remove locale-specific stop words
2. **Score** each candidate song:
   - `keywordCoverage` = fraction of user keywords found in title tokens (0.0-1.0)
   - `titleCoverage` = fraction of title tokens covered by user keywords (0.0-1.0)
   - `score` = `(0.7 * keywordCoverage + 0.3 * titleCoverage) * 100`
   - Positional bonus: +5 if user keywords match starting from the first title token
   - A song must have `keywordCoverage == 1.0` (all user keywords must appear) to be considered a match

## API

```csharp
public static class KeywordMatcher
{
    public static string[] Tokenize(string text, string locale);
    public static List<(BaseItem Item, double Score)> Score(
        IReadOnlyList<BaseItem> songs, string[] keywordTokens, string locale);
    private static readonly Dictionary<string, HashSet<string>> StopWords;
}
```

## Stop Words (inline, keyed by locale prefix)

- **en-US/en-GB/en-AU/en-CA/en-IN:** the, a, an, of, in, on, at, to, and, or, is, it
- **it-IT:** il, lo, la, i, gli, le, di, del, della, un, una, in, su, per, con, da, e, o, che
- **de-DE:** der, die, das, ein, eine, und, oder, in, an, auf, zu, von, mit
- **fr-FR/fr-CA:** le, la, les, un, une, des, de, du, en, dans, sur, et, ou

## Unit Tests

- Tokenize: stop word removal, punctuation handling, locale-awareness
- Score: keyword coverage, title coverage, positional bonus
- Edge cases: empty input, single word, stop-words-only (returns empty array), very long input

## Design Spec

See `docs/superpowers/specs/2026-06-03-find-song-keyword-search-design.md` sections "Search Algorithm" and "KeywordMatcher API".
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 KeywordMatcher.Tokenize strips stop words per locale, handles punctuation, lowercases
- [ ] #2 KeywordMatcher.Score returns all songs where keywordCoverage == 1.0, sorted by score descending
- [ ] #3 Positional bonus (+5) applied when user keywords match from first title token
- [ ] #4 All unit tests pass: tokenization, scoring, edge cases
- [ ] #5 dotnet build 0 errors, 0 new warnings
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented KeywordMatcher static utility with Tokenize (locale-aware stop word removal, punctuation splitting, lowercase) and Score (keyword coverage + title coverage formula with positional bonus). 38 unit tests covering tokenization, scoring, edge cases. Simplify pass eliminated redundant HashSet allocations and overcomplicated HasPositionalMatch method.
<!-- SECTION:FINAL_SUMMARY:END -->

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
