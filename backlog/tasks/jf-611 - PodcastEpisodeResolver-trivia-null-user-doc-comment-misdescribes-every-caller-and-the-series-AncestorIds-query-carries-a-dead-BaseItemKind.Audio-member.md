---
id: JF-611
title: >-
  PodcastEpisodeResolver trivia: null-user doc comment misdescribes every
  caller, and the series AncestorIds query carries a dead BaseItemKind.Audio
  member
status: To Do
assignee: []
created_date: '2026-09-20 19:20'
labels: []
dependencies: []
references:
  - commit eefb09ec
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PodcastEpisodeResolver.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of eefb09ec (JF-599) trivia in the new shared resolver (both confirmed, both below the review's reporting cap): (1) PodcastEpisodeResolver.cs:36 doc says the jellyfinUser param may be null 'e.g. the APL tap path', but the APL tap path resolves and passes a non-null user and BaseHandler.ResolveJellyfinUser can never return a null user with a null error, so no current caller can pass null; the dead null-skip clause plus its wrong example invites a future caller to drop user scoping. (2) The series-shape query's IncludeItemTypes carries BaseItemKind.Audio alongside Episode, but no real storage shape puts an Audio item under a Series ancestor (live-probed on the production box: Podcasts library Audio=0, Shows library Audio=0, all Audio lives under Music with Series=0), so the Audio arm is dead predicate weight that misdocuments the data model the resolver was written to encode. Runtime cost is ~zero (one extra IN-list member, Limit 1). Also noted in review and dismissed as accepted repo style (unverified, zero behavioral effect): the two new 'word - word' hyphen comment lines in PlayPodcastIntentHandler.cs:184/186, which the user-level manual's prose rule bans; include or explicitly dismiss per maintainer preference.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 BuildLatestEpisodeQuery's jellyfinUser doc describes reality (all three callers pass a resolved non-null user; BaseHandler.ResolveJellyfinUser never returns null user with null error)
- [ ] #2 The series-shape IncludeItemTypes decision is deliberate and documented, or the Audio member is removed with the data-model reasoning in the comment
- [ ] #3 No test changes needed (the series-shape mock keys on Episode only, verified in the review); full suite green
- [ ] #4 While in the file: the two added comment lines using the 'word - word' parenthetical-hyphen shape (PlayPodcastIntentHandler.cs:184/186) are reworded per the user-manual prose rule, or noted as accepted repo style if that is the maintainer's call
<!-- AC:END -->

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
