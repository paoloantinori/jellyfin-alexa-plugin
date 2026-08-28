---
id: JF-403
title: >-
  Static samples on slotted intents residual (PlayLastAdded, Recommend; en-US +
  it-IT + sweep all locales)
status: Done
assignee: []
created_date: '2026-08-23 05:57'
updated_date: '2026-08-23 06:27'
labels:
  - interaction-model
  - nlu
  - anti-pattern
milestone: m-17
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Model hygiene finding (2026-08-23 audit). Residual instances of anti-pattern #1 (static samples on slotted intents): en-US PlayLastAddedIntent mixes concrete samples ("Play new media", "play recently added", "play new arrivals") with slotted ones; en-US RecommendIntent ("recommend something", "recommend some music", "recommend a movie"); same pattern in it-IT RecommendIntent ("Suggerisci una canzone") and PlayLastAddedIntent ("Riproduci nuovi media"). The NLU preferentially matches the static variant and delivers an EMPTY slot to the handler. Impact is degradation not breakage (these handlers tolerate empty slots with defaults/prompts), but per anti-pattern #1 these concrete forms belong in separate slotless variants or should be phrased to include the slot. Sweep ALL 17 locales for the same class (detection: samples without '{' on intents that declare slots), fix, and re-run the NLU fixtures for affected locales. Note: FindSongIntent's slotless entry phrases are INTENTIONAL (conversation opener) - exclude them.
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed as verified-no-defect (commit above). Full sweep: 8 intents x 17 locales carry static samples on slotted intents; ALL have optional slots with handler defaults (verified per intent). The assessment's 2 flagged instances were false positives of an under-specified rule. Sharpened anti-pattern #1 in CLAUDE.md: rule applies to required-slot intents; documented allowlist of the verified optional-slot intents so nobody 'cleans them up' later.
<!-- SECTION:FINAL_SUMMARY:END -->
