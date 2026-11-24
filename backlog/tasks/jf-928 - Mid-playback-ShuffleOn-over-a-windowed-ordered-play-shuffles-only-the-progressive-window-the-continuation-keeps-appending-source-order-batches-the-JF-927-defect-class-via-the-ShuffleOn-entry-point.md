---
id: JF-928
title: >-
  Mid-playback ShuffleOn over a windowed ordered play shuffles only the
  progressive window: the continuation keeps appending source-order batches (the
  JF-927 defect class via the ShuffleOn entry point)
status: Done
assignee: []
created_date: '2026-11-15 15:27'
updated_date: '2026-11-24 22:42'
labels: []
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ShuffleOnIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/QueueContinuation.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-927 altitude review (uncommitted diff on AlbumPlayService.cs). JF-927 fixed the LAUNCH-time instance: the shuffle snapshot now derives from the full playlist and the shuffle arm clears/skips the continuation. But the SAME user-visible defect class is reachable through mid-playback shuffle-on:

1. User plays a big playlist ORDERED: the launch installs the 5-track progressive window and seeds a QueueContinuation with Shuffle unset (false) (AlbumPlayService.cs ~1276-1400).
2. Mid-playback the user says shuffle: ShuffleOnIntentHandler.cs:112 calls DeviceQueueManager.ShuffleRemaining, which physically reshuffles ONLY the live window queue (DeviceQueueManager.cs:2289). The handler never touches QueueContinuationStore (no reference in the file), so the stored continuation keeps Shuffle=false.
3. Because the queue now has OriginalItemIds set, ProgressReporter.ResolvePlaybackOrder returns reshuffledQueue=true and ResolveNextItemId (PlaybackNearlyFinishedEventHandler.cs:625) advances SEQUENTIALLY.
4. When the shuffled window drains, TryFetchContinuationBatch passes the JF-674 identity gate (the minted window page is a SUBSET of the live queue; IsForLiveQueue is membership-shaped, QueueContinuation.cs:133), fetches the next batch with Shuffle=false (no Shuffler.ShuffleCopy, PlaybackNearlyFinishedEventHandler.cs ~450), and appends the SOURCE-ORDER tail. The resolver then plays it sequentially.

Net effect: shuffle-on mid-playback of a big playlist = a shuffled handful, then the rest in source order. Same symptom as issue #44, different entry point, so the JF-927 fix cannot cover it (ShuffleOn does not route through BuildPlaylistPlayResponseAsync).

Design note for the implementer: QueueContinuation is init-only, so flipping the flag needs a store-level update/re-mint API or a re-Set of the entry. The designed channel for shuffle semantics on appended batches already exists and is honored by the fetcher (the artist path mints Shuffle = _config.ShuffleArtistSongs at PlayArtistSongsIntentHandler.cs:611 / CrossMediaFallback.cs:519), so aligning ShuffleOn/Off with that channel is the natural shape. Per-batch shuffle (like the artist path) is acceptable scope; a global reshuffle of the remaining source is not required.

Related consolidation observed in the same review (optional, can fold into this task or skip): the five continuation mint sites (playlist ordered AlbumPlayService.cs:1382, album ~956, artist PlayArtistSongsIntentHandler.cs:600 / CrossMediaFallback.cs:508, audiobook AudiobookPlayResolver.cs:522) each decide seed-or-nothing; when a relaunch window swallows the whole source (InitialPageHasMore false) they leave a stale entry in place, leaning on the JF-674 consumption gate, which is subset-blind for same-source mutations (removed tracks can be re-appended as unseen from stale CachedTracks). A shared seed-or-clear seam would own that decision once.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduced/pinned by test: an ordered playlist play larger than the initial window followed by AMAZON.ShuffleOnIntent must not play any post-window track in source order
- [ ] #2 The fix updates the stored continuation's shuffle semantics (or re-mints/removes the entry) from BOTH ShuffleOnIntentHandler and ShuffleOffIntentHandler, restoring ordered batches on shuffle-off
- [ ] #3 ShuffleOff after the fix still restores pre-shuffle queue order (RestoreOrder path unaffected)
- [ ] #4 Unit tests cover: shuffle-on mid ordered window play, shuffle-off revert, and the no-continuation (short queue) shape
- [ ] #5 No regression in the JF-927 launch-time shuffle tests (AlbumPlayServicePlaylistShuffleTests)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: /simplify on the review-round delta only (the five a1eb8790 fixes are review-driven: a SourceType gate, a doc rewrite, a test-equality fix, a message update, banned-dash removals); the worker's own 4-agent /simplify covered the base implementation 913f0b8c; the /code-review evidence (worker high + orchestrator medium, all findings applied) is in this transcript.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped 2026-11-24 via the merge of worktree-agent-ada29ecdc1f9aacaf (base 913f0b8c + review round a1eb8790). QueueContinuationStore.TryUpdateShuffleForLiveQueue: the JF-674 identity-gated, idempotent flip that ShuffleOn/Off handlers now run, RESTRICTED TO THE MUSIC SOURCETYPES (Playlist/Album/Artist) after the review's blocking finding: an Audiobook continuation lives legitimately on the AudioPlayer path (screenless chapter-resume) and a mid-book shuffle-on could have scattered chapters; the store is the single owner of the rule. QueueContinuation.WithShuffleSemantics is the re-mint clone (identity, offsets, cached source carried; a reflection completeness guard with sequence-equality for collections). Per-batch shuffle only, riding the artist path's existing fetcher channel; the JF-713 KNOWN RACE doc names both failure modes honestly (lost offset advance bounded by the id-set dedupe; lost flip = one batch in source order, self-heals next batch; non-locking kept per precedent). Tests: the AC-1 defect pin end-to-end (17-track ordered play, window edge, the 12-track tail appended shuffled at 1/12! probability), off-revert, short-queue shape, foreign no-mutation, audiobook-never-scattered, music-sources flip theory; JF-927 launch tests green. 6254/6254 both TFMs. Gates: worker /simplify (4 agents) + /code-review high (6 triaged) in its session; orchestrator Skill(code-review) medium (5 findings, all applied in a1eb8790); the claimed unrelated net9 flake was re-run green before merge (both TFMs isolated).
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
