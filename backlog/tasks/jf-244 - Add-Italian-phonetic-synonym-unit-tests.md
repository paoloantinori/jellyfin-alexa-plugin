---
id: JF-244
title: Add Italian phonetic synonym unit tests
status: Done
assignee: []
created_date: '2026-06-02 19:32'
updated_date: '2026-06-02 19:46'
labels:
  - enhancement
  - it-IT
  - testing
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
All other phonetic synonym generators (FR, DE, ES, PT, JA, NL) have dedicated test files, but ItalianPhoneticSynonyms.cs has no corresponding ItalianPhoneticSynonymsTests.cs. Add unit tests covering the Italian phonetic transform rules (th→t, ph→f, sh→sc, silent h, w→v, article "il"/"i"), following the same pattern as FrenchPhoneticSynonymsTests.cs.

Files:
- Source: `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/ItalianPhoneticSynonyms.cs`
- Reference test: `Jellyfin.Plugin.AlexaSkill.Tests/Unit/FrenchPhoneticSynonymsTests.cs`
- New test: `Jellyfin.Plugin.AlexaSkill.Tests/Unit/ItalianPhoneticSynonymsTests.cs`
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
Added ItalianPhoneticSynonymsTests.cs with 47 unit tests covering all phonetic transforms, Italian origin detection, article handling, band name prefix, dispatch integration, and edge cases. Build clean, all 2131 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->
