---
id: JF-646
title: >-
  JF-646 - catalog-side katakana synonyms: Latin artist/album names get kana
  variants in the ja catalog upload so NLU selection resolves naturalized ja
  voice (the routing-layer complement to JF-643)
status: To Do
assignee: []
created_date: '2026-09-27 08:01'
updated_date: '2026-09-27 10:49'
labels:
  - catalog
  - i18n
  - ja-JP
  - nlu
dependencies:
  - JF-642
  - JF-643
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
  - >-
    backlog/tasks/jf-643 -
    JF-643-katakana-query-values-never-match-Latin-library-names-script-gap-in-fuzzy-phonetic-search-naturalized-ja-JP-artist-and-genre-requests-all-end-not-found.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-643 altitude review: the routing-layer complement to the query-side romanizer.

THE GAP: query-side romanization (JF-643) fixes everything AFTER the request reaches a handler, but NLU SELECTION is untouchable from there. JF-642's own evidence: en-US/hi-IN select PlayArtistSongs because catalog-backed musician ER wins selection (JellyfinArtist catalog with phonetic synonyms), while ja's katakana loses selection to the free-text genre slot (and will keep losing to OTHER competition even after JF-642 fixes the genre steal). For a ja user, artist selection needs the catalog to carry KATAKANA forms of the library's Latin names so AMAZON.Musician (or a catalog-backed type) resolves them NLU-side.

THE WORK: extend the catalog synonym pipeline with a Japanese generator: Latin name -> katakana rendering(s), coverage-oriented (one-to-many is fine; extra near-miss synonyms are harmless to ER, the JF-362 principle), capped like the Romance generators (per-name cap, SlotValueHelper.Truncate, the 140-char limit). The reverse direction of KatakanaRomanizer: syllabary-driven Latin->kana with vowel insertion; long-vowel mark handling (drop, matching the romanizer's contraction so クイーン and Queen round-trip); germinate/nasal edge cases. Wire into PhoneticSynonymGenerator's dispatch (the JapanesePhoneticSynonyms.cs file is catalog-side Latin-to-Latin today: si->shi etc.; the kana generator complements it for the ja-JP locale's catalog upload). Watch the ar-SA catalog-wiring exclusion (JF-543) and the CatalogSyncLocales default.

INTERPLAY: JF-642 (custom genre type) + this task together close naturalized ja voice end-to-end: JF-643 bridges search, JF-642 stops the genre steal, this makes NLU selection resolve katakana artist names via ER so the request reaches the right intent with a resolved entity. Verify with profile-nlu ja probes: クイーン carriers select PlayArtistSongs with ER_SUCCESS_MATCH on the JellyfinArtist authority (the en-US/hi-IN shape from the JF-642 investigation).

VERIFICATION BAR: profile-nlu selection probes as above; catalog upload logs show the kana variants appended; no regression in the other 16 locales' catalog builds (the generator is ja-locale-scoped).
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 PRIORITY RAISED to high + scope note: the JF-642 live battery refuted the custom-type steal fix at NLU selection (single-word katakana still selects PlayByGenre deterministically; the evidence is in JF-642's notes), so THIS task is now the demonstrated routing-layer fix for the steal, not a complement: with katakana variants in the ja JellyfinArtist catalog, AMAZON.Musician ER resolves naturalized ja artist names and wins selection the way en-US/hi-IN do today. The description's interplay sentence (JF-642 stops the genre steal) is superseded by JF-642's battery note.
<!-- SECTION:NOTES:END -->
