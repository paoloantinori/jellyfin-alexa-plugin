---
id: JF-484
title: >-
  JF-474 review polish: RadioStarted count convention, shared shuffle-and-cap
  helper, ApplyLibraryFilter logger arg
status: Done
assignee: []
created_date: '2026-09-04 12:22'
updated_date: '2026-09-04 15:25'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All three items landed (commit 0389b24e), deployed, and live-smoked.

1. The 'Found N similar tracks' count is EXCLUDES-the-playing-track on both seed paths, enforced structurally: the announcedCount parameter is REMOVED and derived once inside StartRadioPlayback (queue.Count - 1), so the off-by-one the genre path carried cannot return with a future caller. The genre seed log now reports the capped queue length. The sweep found no third speaking site (TurnRadioOn/Off speak no count; the AutoPopulate paths log addedCount only).
2. BaseHandler.ShuffleAndCap<T> folds the four shuffle-and-cap copies (context 20, genre 20, continuation 15, PostPlay 15); bit-identical at every site (the old code copied-then-shuffled-then-truncated; the helper is ShuffleCopy + the same RemoveRange); no fifth copy exists, and the two cap-less shuffle sites are deliberately untouched.
3. QueryRadioChannelsAsync passes Logger to ApplyLibraryFilter (log parity with the siblings).

Two count pins (each asserting the new count present and the old absent; both deterministic under Random.Shared because the count is order-invariant for the fixture queues); three mutations: either path's flip fails exactly its pin, the derived single-site flip fails both. Suite 3171/3171, Release 0 warnings, validators baseline. Review: zero findings >= 80 (the single-track 'Found 0' edge scored ~35: truthful under the convention and a degenerate-library state; the Contains+DoesNotContain pair judged non-redundant: it is what makes the mutations bite singly).

Deploy: config survived, the PauseKeepsSession flag still on, and the genre-radio smoke plays with the announcement riding the new unified count (Audio directive; the it-IT announcement is silent per AnnounceAudioPlays default, consistent with the earlier JF-474 verification).
<!-- SECTION:FINAL_SUMMARY:END -->
