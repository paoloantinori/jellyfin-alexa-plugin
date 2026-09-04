---
id: JF-484
title: >-
  JF-474 review polish: RadioStarted count convention, shared shuffle-and-cap
  helper, ApplyLibraryFilter logger arg
status: In Progress
assignee: []
created_date: '2026-09-04 12:22'
updated_date: '2026-09-04 14:58'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayRadioIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Below-cap observations (70/65/60) from the JF-474+JF-482 combined /simplify + code-review pass (2026-09-04), filed per the same-turn tracking rule so they are not lost. All are polish; none block the commit.

1. RadioStarted count semantics (~70): the genre-seeded path (PlayRadioIntentHandler.StartGenreRadio) passes announcedCount = shuffled.Count, which COUNTS the track that is about to play, while the context-seeded path passes queue.Count - 1, which EXCLUDES it. The spoken "radio started with N tracks" is off by one between the two paths for the same real queue length. Pick one convention (excluding the playing track matches the existing context-path wording and the pre-change behavior) and pin both with assertions on the formatted count.

2. Shuffle-and-cap idiom duplication (~65): `ToList(); Shuffle(x); if (x.Count > cap) x.RemoveRange(cap, x.Count - cap)` now exists at PlayRadioIntentHandler (context path, cap 20), PlayRadioIntentHandler.StartGenreRadio (cap 20), PlaybackNearlyFinishedEventHandler.AutoPopulateRadioTracks (cap 15), and the PostPlay variant. A tiny shared helper (e.g. BaseHandler.ShuffleAndCap(list, cap)) would fold four copies; caps differ so the cap must be a parameter.

3. ApplyLibraryFilter logger arg (~60): PlayRadioIntentHandler.QueryRadioChannelsAsync calls ApplyLibraryFilter(query, user, _libraryManager) without the optional ILogger, while sibling helpers (GetArtistSongsAsync, SearchItemsFuzzyAsync) pass Logger. Pass it for diagnostic parity.

Do NOT fold the LaunchChannelAsync vs PlayChannelIntentHandler duplication into this task; that is JF-483, already tracked separately.
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
