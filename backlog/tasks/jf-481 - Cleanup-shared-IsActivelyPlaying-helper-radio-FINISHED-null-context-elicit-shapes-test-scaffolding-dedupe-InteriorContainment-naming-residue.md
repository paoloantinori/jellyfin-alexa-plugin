---
id: JF-481
title: >-
  Cleanup: shared IsActivelyPlaying helper, radio FINISHED/null-context elicit
  shapes, test scaffolding dedupe, InteriorContainment naming residue
status: To Do
assignee: []
created_date: '2026-09-04 10:53'
labels: []
dependencies: []
references:
  - JF-478/479/480 review pass (2026-09-04)
  - PlayRadioIntentHandler.cs
  - PlaybackFinishedEventHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Landing spot for the below-threshold items from the JF-478/479/480 review pass (2026-09-04), per the every-recommendation-lands rule:

1. Radio shared helper: the actively-playing idiom (playerActivity == PLAYING || BUFFER_UNDERRUN) now exists verbatim in PlayRadioIntentHandler.cs ~:89 and PlaybackFinishedEventHandler.cs ~:77. Extract BaseHandler.IsActivelyPlaying(context); keep ResumeIntentHandler's intentionally narrower PLAYING-only check separate (document why in the helper doc). Two-site lockstep change risk otherwise.
2. Radio elicit residual shapes (both self-recover, scored ~55-60, unverified on device): (a) playerActivity FINISHED (natural queue exhaustion) with a multi-room surviving session item now elicits instead of seeding radio from the item; (b) a request whose context carries no AudioPlayer object at all while a group member actively plays. Decide whether FINISHED should seed radio (probably yes: the queue just ended, radio continuation is the PostPlay-adjacent behavior) and verify on device.
3. Test scaffolding dedupe: the two StartsRadioMode radio tests duplicate the library/user mock pair; the three new CrossMediaTypeFallbackTests repeat the album/artist/tracks triage callback; the two PlayAlbum mirrors repeat the mock pair. Local helpers would dedupe. Also cosmetic: the two JF479 CrossMediaTypeFallbackTests comment blocks have continuation lines at 4-space indent under an 8-space body.
4. Naming residue: the historical test method names carrying InteriorContainment_ (PlaySong_InteriorContainmentAlbum_NotSubstituted and the JF-478-era pins) predate the IsEmbeddedContainment rename; rename for grep-coherence or leave with a note on the renamed method that the old class name was 'interior containment' (the production API is already renamed).

Consolidation/cleanup only: no behavior change except item 2a if the FINISHED decision lands.
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
