---
id: JF-538
title: >-
  SearchMedia PlayItem: the last video-launch announce site not on the
  progressive vehicle (HandleFuzzyMiss sync delegate)
status: Done
assignee: []
created_date: '2026-09-10 13:26'
updated_date: '2026-09-10 18:32'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge 1dd89014 + deployed (md5 7602d981 verified, config survived, audio smoke green, zero FTL). The last video-launch announce site is on the JF-501 progressive vehicle. Option (a) landed (option (b) impossible: PlayItem is sync, the item known only inside the delegate): HandleFuzzyMiss async with Func<T, Task<SkillResponse>> delegate, 9 call sites converted mechanically (the 8 side-effect delegates await + Task.FromResult(null!) preserving the sentinel; SearchMedia and PlaySong return real responses), PlayItem async routing through BuildVideoAppLaunchResponseAsync with all guards inherited; AplUserEventHandler stays sync by design. Gates: /simplify (worker-integrated; combined gate), code-review high (5 axes verified: no fire-and-forget, ordering exact, sentinel exact, zero sync-over-async, ConfigureAwait throughout) with its ONE finding APPLIED: the qualifier-band double-announce (progressive now-playing + qualifier on final response, pre-diff the qualifier silently replaced the announce; the qualifier was also still cuttable) - fixed by making the qualifier ride the progressive vehicle when the play response is directive-only and context/request are passed (optional params, one call site), classic overwrite preserved for all other callers, band pinned by the Rhapsoy/Rhapsody-71 harness test; the review's count correction (PlaySong also returns a real response) recorded in the commit. Suites 3580, 3580, 3581 worktree / 3582 post-merge main net9.0; 0 warnings both TFMs. Device check rides the JF-501 checklist (announce completes before playback on a fuzzy-miss video auto-play too).
<!-- SECTION:FINAL_SUMMARY:END -->
