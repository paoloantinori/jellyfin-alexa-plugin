---
id: JF-611
title: >-
  PodcastEpisodeResolver trivia: null-user doc comment misdescribes every
  caller, and the series AncestorIds query carries a dead BaseItemKind.Audio
  member
status: Done
assignee: []
created_date: '2026-09-20 19:20'
updated_date: '2026-09-21 10:03'
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
- [ ] #3 Test changes limited to repinning the series-shape assertion (Episode present, Audio deliberately absent) and its doc; full suite green
- [ ] #4 While in the file: the two added comment lines using the 'word - word' parenthetical-hyphen shape (PlayPodcastIntentHandler.cs:184/186) are reworded per the user-manual prose rule, or noted as accepted repo style if that is the maintainer's call
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-09-21 (commit 18cd96d2). Resolver trivia fixed: jellyfinUser params non-nullable with reality docs + ArgumentNullException.ThrowIfNull (machine-checked contract, the RetryHelper idiom); the series query drops the dead Audio member with the live-probed reasoning in the comment; the album arm takes the same Limit 1 + IsVirtualItem=false (a 1500-track community album no longer materializes fully inside the Alexa budget); the TvNextUpService parity claim corrected to the single DateCreated-desc core; the test doc + assertion repinned Episode-present/Audio-absent. The review sweep surfaced and fixed a PRE-EXISTING flag hole: PodcastsEnabled now gates the yes-confirm and the APL series tap too (only the intent handler was gated). AC#3's original 'no test changes needed' was false and corrected in the record. F2 kept-documented (an undocumented third-party Audio-under-Series shape now gets a clean spoken refusal instead of playing); F4 skipped (the query-shape assertion is the executable record). Gates: literal /code-review high (8 findings) + literal /simplify (1 finding applied); tests 4189/4189 both TFMs; Release+warnaserror clean.
<!-- SECTION:FINAL_SUMMARY:END -->

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
