---
id: JF-227
title: Use Alexa Intent History to discover missing utterance variants
status: Done
assignee: []
created_date: '2026-05-29 15:10'
updated_date: '2026-06-08 11:48'
labels:
  - enhancement
  - NLU
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Amazon's Intent History feature shows what real users say (anonymized, aggregated) when interacting with the skill, including utterances that fall through to FallbackIntent. Build a workflow or script to: 1) Query Intent History via SMAPI for each locale, 2) Identify high-frequency utterances routing to wrong intents or FallbackIntent, 3) Surface candidate utterances to add to interaction models. This is Amazon's recommended practice for improving NLU accuracy. See https://developer.amazon.com/en-IN/blogs/alexa/alexa-skills-kit/2020/01/improving-nlu-accuracy-of-alexa-skills. Requires the skill to have enough users per locale (Amazon threshold: 10 unique users/day for data to appear).
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Blocked by design: private skills cannot access Intent History API (requires 10+ unique users/day). This task should never have been created for a private skill.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
NOT APPLICABLE — This skill is private and will never meet Amazon's 10+ unique users/day threshold required for Intent History data. The SMAPI Intent History API returns 404 for all locales. No code changes made; script created then deleted.
<!-- SECTION:FINAL_SUMMARY:END -->
