---
id: JF-714
title: >-
  JF-714 - gate the five screenless-Tell video sites' NowPlaying writes on
  HasLaunchDirective (phantom state on screenless devices)
status: To Do
assignee: []
created_date: '2026-10-02 15:20'
labels:
  - playback
  - refusal-contract
  - bug
dependencies:
  - JF-699
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-699 merge
(commit 65821618, finding 2 of 5). Five handlers write session.NowPlayingQueue /
FullNowPlayingItem after builder calls that can still return a NON-LAUNCH capability
Tell with a configured secret: BuildVideoAppLaunchResponse returns the
VideoRequiresScreen Tell on screenless devices (PlaybackLaunchBuilder.cs:969-976), so
"play random movie" / "recommend a movie" / "search for <movie>" / the video confirm
arm on a screenless Echo (Dot) write NowPlaying state for a launch that will not
happen; a later "what's playing" answers the phantom movie and next/previous advance a
queue that never started. Pre-existing behavior (the writes ran before the build
before), but the JF-699 diff moved exactly these lines and StartOverIntentHandler
keeps a HasLaunchDirective gate for the identical shape, whose comment labels this
case NOT tautological.

SITES: PlayVideoIntentHandler.cs ~238, SearchMediaIntentHandler.cs ~577,
PlayRandomIntentHandler.cs ~197, RecommendIntentHandler.cs ~221 (the movie arm whose
in-code note documents the skip), YesIntentHandler.cs ~470.

THE WORK: wrap each site's two writes in `if
(PlaybackLaunchBuilder.HasLaunchDirective(response))` (the StartOver pattern; audio
and real-video paths carry directives so their writes are unaffected). Add ONE pin:
the screenless movie arm (any of the five sites with the cheapest existing harness)
returns the capability Tell AND leaves NowPlayingQueue/FullNowPlayingItem unset; the
launch path still writes. Verify no existing test pins the phantom-write behavior
(search for the screenless Tell in the five handlers' test classes before flipping).
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
