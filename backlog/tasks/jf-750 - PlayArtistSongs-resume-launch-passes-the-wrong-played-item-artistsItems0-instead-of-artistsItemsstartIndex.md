---
id: JF-750
title: >-
  JF-750 - PlayArtistSongs resume launch passes the wrong played item
  (artistsItems[0] instead of artistsItems[startIndex])
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - playback
  - bug
dependencies: []
references:
  - >-
    backlog/tasks/jf-674 -
    JF-674-stale-queue-continuations-inject-mid-playlist-content-into-a-later-unrelated-single-item-playback-no-queue-identity-validation.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 from the JF-674 /code-review high round (the
review-recommendation rule: the finding is real, outside the shipped change's
surface, and lands in the tracker the same turn). Independently confirmed by
both review passes AND verified in source by the JF-674 worker before filing.

THE FINDING: `PlayArtistSongsIntentHandler.HandleAsync`'s bulk-play arm
(Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
~:580) builds its launch with `artistsItems[0]` as the played ITEM while the
stream URL and token id come from `artistsItems[startIndex]` (itemId at ~:574):

  SkillResponse response = Launch.BuildAudioPlayerResponse(
      PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId,
      artistsItems[0], user, context, announceLocale: locale);

The twin site the handler shares the shape with
(CrossMediaFallback.BuildArtistSongsResponseAsync ~:379) passes
`sortedItems[startIndex]`, proving the intended form. Two lines later the same
handler sets `session.FullNowPlayingItem = artistsItems[startIndex]`, so the
launch metadata and the session pointer disagree exactly on the resume shape.

WHEN IT BIT: only a resumed artist play (SortAndFindResumeIndex returns
startIndex > 0) with `_config.ShuffleArtistSongs` off (shuffle resets
startIndex to 0, masking the bug). The launched track's AudioItem metadata
(title/subtitle the native now-playing surface renders), the now-playing
announce, and any metadata-driven surface name TRACK 1 while track N's stream
plays. The stream itself, the queue, and continuation are all correct
(keyed on itemId), so this is a wrong-metadata bug, not a wrong-audio bug.

FIX SHAPE: pass `artistsItems[startIndex]` (one line), mirroring the
CrossMediaFallback twin. A pin should drive PlayArtistSongs with a resume
position (a played first track, startIndex 1) and assert the response's
AudioItem metadata carries track 2's name.

NOT FILED (refuted during verification, recorded so the next round does not
re-derive it): the same review round claimed PlayBookIntentHandler crashes
when FindResumeTrackIndex returns startIndex == trackItems.Count (a fully
played first page). REFUTED at ResumeMath.cs:397: the
`(lastPlayedIndex + 1, 0)` return is guarded by
`lastPlayedIndex + 1 < tracks.Count`, and the fully-played-page shape falls
through to `(0, 0)`; no current producer returns an out-of-range startIndex.
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
