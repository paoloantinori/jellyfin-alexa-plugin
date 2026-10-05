---
id: JF-627
title: >-
  Unify the queueManager null contract between ResolvePlayingMedium and
  ResolveCurrentPlayingItem (or make the playlist-edit no-ledger policy
  deliberate)
status: Done
assignee: []
created_date: '2026-09-25 01:40'
updated_date: '2026-10-06 00:00'
labels:
  - tech-debt
  - refactor
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaylistEditHandlerBase.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-626 /simplify altitude review (2026-09-25), same-turn filing rule.

JF-626 consolidated the three current-item resolvers into PlaybackLaunchBuilder.ResolveCurrentPlayingItem, which deliberately kept a pre-existing contract divergence: in the SAME class, ResolvePlayingMedium treats queueManager=null as "fall back to Plugin.Instance's DeviceQueueManager" while ResolveCurrentPlayingItem treats null as "disable the ledger arms entirely". The no-ledger policy has exactly one customer (the playlist-edit family: AddCurrentToPlaylist/RemoveCurrentFromPlaylist via PlaylistEditHandlerBase.ResolveCurrentItem) and exists only because that family never took a DeviceQueueManager ctor param; it was an accident RateItem's pre-JF-626 doc even misdocumented. JF-626's mitigation: the queueManager parameter has NO default, so every caller states its choice explicitly.

User-visible inconsistency this perpetuates: during a VideoApp launch of a movie/book/seek-mode song, "repeat this" and "rate this" resolve the displaced item via the ledger, but "add this to playlist X" resolves the stale pre-launch AudioPlayer token item instead.

The decision to make (behavior change, so NOT folded into the JF-626 refactor): either (a) unify the null contract on the Plugin.Instance fallback (ResolvePlayingMedium's shape) so the playlist-edit family gains the full displacement arbitration with no ctor change (DeviceQueueManager is a registered DI singleton; handlers are auto-discovered, or the family can take it by ctor), accepting and testing the playlist-edit behavior change; or (b) keep the divergence deliberately and document it in CLAUDE.md as policy. Also consider a lockstep test enumerating item-kind x route asserting the resolver's displacement decision equals the classifier's non-Audio classification (the JF-625 miss-class guard; the kernel extraction ClassifyLedgerItemKind already shares the ladder, so this is belt-and-braces).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Also fold in the JF-626 review finding 7 decision: the shared resolver's FullNowPlayingItem leg returns the session-held item WITHOUT a library existence check, so a track deleted mid-playback while the session still holds it (and no token resolves) now flows to the playlist-edit family too (pre-JF-626 their DTO path went through GetItemById and answered NoMediaPlaying for a deleted item; the un-guarded semantics was previously scoped to Repeat/RateItem). JF-626 accepts this as the deliberate free-resolution design (the task's fix (b)); if the tracker decision lands on unifying with the ledger arms, decide here whether the deleted-item-held shape also deserves a guard.

### Design record (2026-10-06)

DECISION: option (a), unify on the Plugin.Instance fallback. Evidence:
1. The no-ledger policy was an accident (the family never took a DeviceQueueManager ctor param; RateItem's pre-JF-626 doc misdocumented it), so option (b) would cement an accident as policy.
2. The divergence is a user-visible WRONG WRITE, not just an inconsistent read: during a VideoApp launch of a movie/book/seek-mode song, "add this to playlist X" adds the stale pre-launch audio item to the user's playlist while "repeat this"/"rate this" resolve the displaced item. A wrong library write is worse than a wrong read.
3. The cost of unification (the resolver's unbounded ledger tail answering an idle device) is exactly the hazard JF-629 already solved for the sibling stateful-write family (favorite): guard at the call site on current evidence.

Shape shipped:
- The ONE ledger read: private ReadLastPlayedSnapshot(context, queueManager) on PlaybackLaunchBuilder resolves the effective manager with the classifier-half null contract (null falls back to Plugin.Instance's) and returns the snapshot; ALL THREE GetLastPlayedSnapshot readers in the class go through it (the medium classifier, the current-item resolver, and the screen-owner belt). The /simplify round extracted it: the first cut had pasted the fallback local verbatim into the resolver, and the altitude review identified the belt's remaining "null disables the BELT" conditional as the same accident class with zero null consumers (all three production callers pass their injected manager; no test calls the belt directly; the SleepTimer suite injects the same manager it swaps), so the belt was unified in the same stroke, behavior-preserving.
- ResolveCurrentPlayingItem's displacementPossible gates on lastPlayedId != null (the evidence) instead of manager presence (the plumbing); outcome-equivalent in every shape (an empty ledger resolved nothing before either). The queueManager parameter keeps NO default (every caller states its choice); its doc states the fallback idiom.
- PlaylistEditHandlerBase takes DeviceQueueManager? by ctor (DI injects the singleton; the sibling handlers' idiom) and passes it through; AddCurrent/RemoveCurrent expose the param; AddSong/CreatePlaylist untouched (they never resolve current).
- The JF-629 idle guard at ResolveCurrentItem consumes the ONE predicate PlaybackLaunchBuilder.HasCurrentPlaybackEvidence (token OR held FullNowPlayingItem OR NowPlayingItem DTO, the resolver's own read set) and answers null (the handlers' existing NoMediaPlaying tell). The three pre-JF-627 DTO-only guards (FavoriteToggle, MediaInfo, ApplyRepeatModeAsync) keep their inline shape for now: migrating them changes behavior on the full-item-without-DTO shape and needs its own red proofs -> FILED as JF-785, with the altitude review's root-fix observation (a ledger recency read would dissolve the whole guard layer) recorded there.
- Six stale param docs on the sibling callers (FavoriteToggle/MediaInfo/MarkFavorite/UnmarkFavorite/RateItem ctor params + ProgressReporter.ApplyRepeatModeAsync) updated from "null disables the ledger arms" to the fallback wording. PlaybackFinishedEventHandler's "no Plugin.Instance fallback" comment is a DIFFERENT mechanism (its own EnqueuedForThisBoundary read over the injected manager) and stays.

JF-626 finding 7 decision (the deleted-item-held shape): NO guard. The FullNowPlayingItem free-resolution is the deliberate JF-626 design shared with Repeat/RateItem; an existence check would reintroduce exactly the per-id library re-resolve JF-626 removed; and the add of a dead id is resolved server-side by Jellyfin's playlist manager. Kept as the free-resolution semantics, documented here.

Code-review round (high, 3 findings, all applied): F1 RateItem's second ledger read (PlayingMediumIsVideoAppAudio, the JF-635 keep-alive check) was a raw _queueManager?.GetLastPlayedSnapshot with no fallback while the rewritten ctor doc promised the fallback for the whole field; rewired through the ONE read (ReadLastPlayedSnapshot made internal), killing the second raw reader and its separate device-key idiom, outcome-equivalent on every shape. F2 the lockstep matrix completed from 13 to the full 15 combos (LiveTvChannel and Audio legacy-null legs added). F3 a parenthetical-hyphen prose violation in the new resolver comment reworded. The reviewer independently verified the full suite green and the Release -warnaserror build clean on the pre-application state.

Lockstep guard added (the belt-and-braces the task asked to consider): Lockstep_DisplacementDecision_EqualsClassifierNonAudioClassification, the full 15-combo matrix (Movie/Episode/LiveTvChannel/AudioBook/Audio x VideoApp/Audio/legacy-null routes; the null-route legs through the hand-written pre-JF-568 JSON, completed to 5 x 3 by the code-review round), asserting for every shape that the resolver returns the ledger item EXACTLY when the classifier answers non-Audio. A future kind or ladder arm added to one reader but not the other fails there.

Red proofs (both TFMs): the two displacement pins RED on the unmodified tree (resolver-level: expected the movie, got null; handler-level: the add carried the stale token item 887e2cc7 instead of the movie). The idle pin GREEN on main (ledger off), RED on the unified-without-guard intermediate (the add carried the days-old id), GREEN with the guard: the guard is load-bearing, not decorative.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DECISION: option (a), unified on the Plugin.Instance fallback; the no-ledger policy was the playlist-edit family's omission, not a policy, and it made "add this to playlist X" during a VideoApp launch add the STALE pre-launch audio item while "repeat this"/"rate this" resolved the displaced item (a wrong library write, verified RED on the unmodified tree: the add carried the token item instead of the movie, both TFMs).

Shipped: the ONE ledger read (ReadLastPlayedSnapshot, null falls back to Plugin.Instance's) now serves every GetLastPlayedSnapshot reader (classifier, item resolver, screen-owner belt, and RateItem's keep-alive check after the code-review round); the belt's residual "null disables the BELT" artifact removed with it, behavior-preserving (zero null consumers). PlaylistEditHandlerBase takes DeviceQueueManager by ctor and guards its stateful write with the ONE evidence predicate HasCurrentPlaybackEvidence (the JF-629 shape; the predicate accepts both session shapes so the JF-626 full-item pin survives). The lockstep theory pins the full 5-kind x 3-route matrix (resolver displacement equals classifier non-Audio). The idle pin proven load-bearing by the two-stage red ladder (green on main, red on unified-without-guard, green with the guard). JF-626 finding 7 (deleted-item-held): no guard, the free-resolution design kept, documented in the design record.

Gates: /simplify 4 angles (6 applied: the ONE ledger read, the belt unification, the ONE evidence predicate, LegacyQueueWith + SwapPluginLedgerScope test hoists, the stale test name, the comment shrink; skips: the two-files-one-store legacy test keeps its distinct scenario, the idle pin's speech assertion matches file idiom); /code-review high 3 findings ALL applied (RateItem's second raw read rewired through the ONE read, the lockstep matrix completed 13 to 15, a prose-hyphen fix); one cut filed same-turn as JF-785 (the three pre-JF-627 DTO-only guards' migration plus the ledger-recency root-fix observation).

Suites: 5338/5338 BOTH TFMs on the final state (5319 baseline + 19 pins); Release --no-restore -warnaserror clean, 0 warnings. DoD N/A items: no session attributes, no HttpClient, no interaction-model or locale changes (existing NoMediaPlaying reused); coverage is the resolver+handler pin ladder (an e2e cannot prime the device ledger mid-session through simulate-skill). Not deployed (worker branch only).
<!-- SECTION:FINAL_SUMMARY:END -->
