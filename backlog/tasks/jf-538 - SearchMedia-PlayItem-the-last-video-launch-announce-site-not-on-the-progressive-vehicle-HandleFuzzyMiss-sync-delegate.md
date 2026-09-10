---
id: JF-538
title: >-
  SearchMedia PlayItem: the last video-launch announce site not on the
  progressive vehicle (HandleFuzzyMiss sync delegate)
status: To Do
assignee: []
created_date: '2026-09-10 13:26'
labels:
  - ux
  - video
  - tech-debt
  - jf501-followup
dependencies: []
references:
  - JF-501
  - 'SearchMediaIntentHandler.cs:456'
  - HandleFuzzyMiss
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-501 implementation (2026-09-10): SearchMediaIntentHandler.PlayItem (Alexa/Handler/Intent/SearchMediaIntentHandler.cs:456) was the ONE video-launch announce site NOT converted to the progressive-response vehicle, because it is invoked through the sync HandleFuzzyMiss auto-play delegate (Func<T, SkillResponse>) and converting it means cascading async through ~9 handlers - out of proportion for the cosmetic fix in that round. It keeps today's announce-on-final-response shape, so a fast-HLS launch reached via the fuzzy-miss auto-play path can still cut the announcement mid-sentence on the Echo Show (the JF-501 device symptom). Scope when taken: either await-unsafe downcast is impossible, so the real options are (a) make the HandleFuzzyMiss delegate async (Func<T, Task<SkillResponse>>) and cascade the ~9 call sites, or (b) give PlayItem a pre-sent progressive announce before the sync delegate runs (split the announce out of the delegate's response, send progressive at the handler entry before invoking the delegate). Evaluate (b) first: smaller, no signature cascade. Verify on-device after (the JF-501 device checklist covers the converted sites; this one rides along).
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
