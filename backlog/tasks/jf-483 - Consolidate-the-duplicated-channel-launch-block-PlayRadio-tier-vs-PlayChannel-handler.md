---
id: JF-483
title: >-
  Consolidate the duplicated channel-launch block (PlayRadio tier vs PlayChannel
  handler)
status: Done
assignee: []
created_date: '2026-09-04 12:13'
updated_date: '2026-09-04 14:56'
labels: []
dependencies: []
references:
  - JF-474 (the duplication site)
  - PlayChannelIntentHandler.cs
  - ILiveTvStreamResolver
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Consolidation candidate flagged during JF-474 (2026-09-04): PlayRadioIntentHandler's new channel-launch tier duplicates PlayChannelIntentHandler's launch block (VideoApp.Launch + the ILiveTvStreamResolver PlaybackInfo URL + device-queue record + the not-available Tell on unresolvable streams). The duplication is marked by a code comment at the JF-474 site. Extract the shared channel-launch helper (BaseHandler or a small collaborator) so the two launch paths cannot drift: a future resolver change or launch-directive fix must land once. Follow the JF-382 precedent for tracked duplication; consolidation only, no behavior change.
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
Implemented, deployed (commit 9da03323).

The diff table found every load-bearing element of the two launch blocks identical (queue set + FullNowPlayingItem before the resolver, the resolver call, the not-available Tell, the device-queue record, the VideoApp.Launch response with ShouldEndSession null); the only difference was PlayRadio's caller-specific entry log, kept at its call site with the same before-session-mutation ordering. The common core is ONE BaseHandler helper (BuildChannelLaunchResponseAsync) owning the three rationale comments once; the resolver is a parameter (the ResolveJellyfinUser pattern; injecting it would churn all 61 handler constructors). PlayChannel delegates after its unchanged search+fuzzy resolution (159->109 lines); PlayRadio delegates after its log line (LaunchChannelAsync deleted; 518->454).

Zero behavior change by construction AND by pin: two new serialization tests drive BOTH handlers' full HandleAsync with identical inputs (different upstream query mocks, so non-vacuous) and assert cross-handler JSON equality on both exits. Suite 3169/3169, Release 0 warnings, validators baseline. Review: zero findings >= 80; the parameter-list and comparer variance both judged consistent with house patterns.
<!-- SECTION:FINAL_SUMMARY:END -->
