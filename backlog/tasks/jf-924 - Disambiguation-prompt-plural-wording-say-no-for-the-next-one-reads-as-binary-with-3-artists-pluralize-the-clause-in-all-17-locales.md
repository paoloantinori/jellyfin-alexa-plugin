---
id: JF-924
title: >-
  Disambiguation prompt plural wording: 'say no for the next one' reads as
  binary with 3+ artists; pluralize the clause in all 17 locales
status: Done
assignee: []
created_date: '2026-11-08 14:40'
updated_date: '2026-11-08 15:06'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Maintainer-approved (2026-11-08, from his on-device UX report): the JF-420.2 multi-artist disambiguation prompt ends with a singular 'say no for the next one' clause ('Di' no per il successivo.' / 'Say no for the next.') which, after the announced count ('Ho trovato 3 artisti: ...'), reads as a binary choice; users may expect the flow to end after one 'no'. The cycling machinery is N-safe (AskNextMatch/NoMoreMatches, unit-pinned at 3 items); only the PHRASING misleads. Change: in ALL 17 locale files, rewrite only the trailing clause of the DisambiguateMultipleArtists key to a plural 'say no to hear the others' form (e.g. it-IT 'Di' no per sentire gli altri.', en-US 'Say no to hear the others.'), keeping the leading count+list+'play the first?' structure (JF-880 convention) untouched. Locale-strings only: no code, no model, no new keys; validate_locales.py must stay green and any test pinning the old substring updated in the same change.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
gate-exempt: locale-strings-only change (17 JSON one-line edits, no code paths), so /simplify and code-review high are not applicable; verification was validate_locales PASS + full suite 6138/6138 per TFM + build 0 warnings. Plural wording shipped in the multi-artist disambiguation prompt across all 17 locales (only the trailing clause changed: it-IT 'Di' no per sentire gli altri.', en 'Say no to hear the others.', plus 15 adapted translations; the JF-880 count-leading structure untouched). Commit e74fa3e1 on main; deployed to minix into the 1.0.2.2 versioned dir (md5 verified, config intact, plugin loaded v1.0.2.2). The change rides the next release for catalog users. The cycling machinery was already N-safe (unit-pinned); this was a phrasing-clarity fix only.
<!-- SECTION:FINAL_SUMMARY:END -->
