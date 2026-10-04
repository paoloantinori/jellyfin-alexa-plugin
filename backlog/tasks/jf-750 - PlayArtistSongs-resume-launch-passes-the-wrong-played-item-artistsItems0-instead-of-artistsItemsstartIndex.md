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

## Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-10-04 by the JF-750 worker (both the fix and the family audit):

THE FIX: `PlayArtistSongsIntentHandler` bulk-play arm passes
`artistsItems[startIndex]` (was `artistsItems[0]`) as the played-item argument
of `BuildAudioPlayerResponse`, so the metadata matches the launched id
(`itemId` already keyed `artistsItems[startIndex]`). Every consumer of that
argument gets the right item by construction (they all read the SAME
parameter): AudioItemMetadata Title/Subtitle (what the Echo's native
now-playing surface renders), the SeekEnabled StandardCard, the opt-in
now-playing announce (`AttachAnnounceIfEnabled` speaks `item.Name`), the APL
NowPlaying directive (`TryAttachNowPlayingDirective`), and the native-controls
VideoApp delegation branch (`BuildVideoAppAudioResponse` receives the same
item). Shuffle stays correct (the shuffle branch resets startIndex to 0, where
old and new expressions coincide). The JF-674 mint below the launch
(`MintedQueueItemIds` wiring, queue/session/continuation writes) is
byte-identical.

RED PROOF (the final test shape, failed on the unmodified base BOTH TFMs
before the fix was applied, twice: first cut and post-gate cut):
`HandleAsync_ResumedQueue_LaunchMetadataMatchesResumedTrack` seeds a 3-song
artist queue with track 1 fully played (startIndex 1), asserts the launched
stream URL carries track 2's id, the session queue head and FullNowPlayingItem
are track 2, and both user-visible observables name track 2: the directive
metadata Title and (announce opted in per the suite convention) the spoken
now-playing speech. Pre-fix failure: Expected "Circles", Actual "Sugar Free
Jazz" (net9.0 and net10.0).

FAMILY AUDIT (the filing's family + the full launch site set, ~45
`BuildAudioPlayerResponse` call sites, re-verified independently by the
/simplify altitude agent): every other site pairs the id and the metadata item
by construction via one of three idioms (derive `itemId = item.Id.ToString()`;
fetch `GetItemById(itemId)`; indexed both-args-`[startIndex]`, e.g.
CrossMediaFallback:375/379 the twin, AlbumPlayService:679/696,
PlayBookIntentHandler:294/352). The YesIntentHandler arms, PlayByDecade,
PlayByGenre, Favorites, Playlist, Random, SkillConnection, LaunchRequest,
ProgressReporter, Resume, ContinueWatching, and the single-item handlers all
launch index 0 with index 0's metadata or a resolved single item. NO second
wrong-item site exists; the JF-750 site was the family's sole divergence.

FILED: JF-758 (the /simplify altitude round's one follow-up: a
Guid-based belt check of the itemId/item pairing at the BuildAudioPlayerResponse
chokepoint, EnsureLaunchResponse style, would catch the next drift of this
class at first fire; dead code on all ~45 sites today, which is why it is
hardening and not part of this fix; its two design constraints, the dashless
"N"-format id at AplUserEventHandler and the deliberate null-item shape, are
recorded in the filing).

Gates: worker Skill simplify (4 angles: reuse 2 applied - the file's own
SetupSongResult seeding helper and the shared TestHelpers.GetPlayDirective;
simplification 1 applied - the non-discriminating DoesNotContain URL assertion
dropped, 1 duplicate deduped with reuse; efficiency CLEAN - the swap is one
O(1) index on a materialized list; altitude CLEAN - the call-site fix is the
right depth, the chokepoint cannot derive or cheaply validate the pairing
without a new dependency and hot-path fetch) + Skill code-review high (no
correctness findings on the fix; 3 applied: the JF-758 file rides this commit,
the Moq user-data seeding converted to one order-independent callback, and the
announce surface pinned so the doc's coverage claim is asserted, not just
read). Suites: 5163/5163 net9.0 AND net10.0 on the final state (main baseline
5162 + 1); Release --no-restore -warnaserror 0 warnings 0 errors. No locale,
model, or speech surface changed.
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror 0/0)
- [x] #2 dotnet test passes (5163/5163 net9.0 AND net10.0)
- [x] #3 No new compiler warnings introduced (0 warnings, -warnaserror)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute surface touched; the pin only asserts existing session state)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient change)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no model change)
- [x] #7 E2E test added for new intent or handler logic (the red-proof pin covers the handler logic: metadata Title + spoken announce on a mid-queue resume; no new intent)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [x] #9 /simplify passed (3 findings applied, 1 borderline skipped with the file-convention reason; efficiency and altitude CLEAN)
- [x] #10 /code-review high passed (no correctness findings on the fix; 3 findings all applied)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Worker cycle complete 2026-10-04: the resume launch passes the item that
matches the launched id (`artistsItems[startIndex]`), red-proven on the
unmodified base both TFMs with the final test shape, the family audited clean
(the filed site was the sole wrong-item launch among ~45 call sites), JF-758
filed for the chokepoint belt-check hardening. Gates simplify + code-review
high run with all findings applied; 5163/5163 both TFMs; awaiting the
orchestrator's merge.
<!-- SECTION:FINAL_SUMMARY:END -->
