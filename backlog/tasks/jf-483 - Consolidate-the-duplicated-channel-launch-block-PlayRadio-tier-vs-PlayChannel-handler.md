---
id: JF-483
title: >-
  Consolidate the duplicated channel-launch block (PlayRadio tier vs PlayChannel
  handler)
status: In Progress
assignee: []
created_date: '2026-09-04 12:13'
updated_date: '2026-09-04 14:09'
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
