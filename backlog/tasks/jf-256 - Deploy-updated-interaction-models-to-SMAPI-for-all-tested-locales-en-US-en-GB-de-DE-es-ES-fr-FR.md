---
id: JF-256
title: >-
  Deploy updated interaction models to SMAPI for all tested locales (en-US,
  en-GB, de-DE, es-ES, fr-FR)
status: Done
assignee: []
created_date: '2026-06-04 11:01'
updated_date: '2026-06-04 13:18'
labels:
  - testing
  - NLU
  - SMAPI
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Deploy updated interaction models to SMAPI for all locales that have NLU test coverage (en-US, en-GB, de-DE, es-ES, fr-FR, it-IT). Currently only it-IT has been deployed with the FindSong changes. The other locales still have old models on SMAPI, so their NLU tests will fail regardless of local model correctness.

Steps:
1. For each locale with test fixtures, build SMAPI payload and deploy via `ask smapi set-interaction-model`
2. Wait for model build to succeed
3. Run NLU tests to verify routing

This is a prerequisite for JF-254 (NLU audit) — you can't verify routing until the models are deployed.
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
Deployed updated interaction models to SMAPI for 6 locales (en-US, en-GB, de-DE, es-ES, fr-FR, it-IT). All models built successfully. NLU tests: 399 passed, 59 failed, 47 skipped, 8 errors. FindSong tests all pass (core JF-251 fix verified). Failures are pre-existing routing issues (MediaInfo slots, RecommendIntent, SearchMediaIntent competition) plus new ShowMoreIntent competing with AMAZON.NextIntent ("avanti"/"next" samples). ShowMoreIntent fix tracked separately.
<!-- SECTION:FINAL_SUMMARY:END -->
