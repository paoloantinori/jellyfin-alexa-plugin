---
id: JF-767
title: >-
  JF-767 - JF-763 review-round out-of-scope findings: the YesIntent confirmation-path
  siblings, and the user-less concat endpoint vs the scoped paged path
status: To Do
assignee: []
created_date: '2026-10-05'
labels: []
references:
  - backlog/tasks/jf-763 - JF-763-JF-757-round-follow-ups-the-residual-hand-kept-album-track-query-shapes-and-the-headtail-library-scope-asymmetry.md
  - backlog/tasks/jf-757 - JF-757-the-album-track-query-shape-is-hand-kept-in-four-copies-head-and-tail-x-ParentId-and-AlbumIds-a-BuildAlbumTracksQuery-builder-would-own-it.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Umbrella for the two real-but-out-of-scope findings of the JF-763 review round (2026-10-05; every claim verified in
source before filing). Both are consequences of JF-757/JF-763 consolidating the paged playback path and the
album-concat endpoint.

## Finding A (moderate): the YesIntent confirmation paths are the unconsolidated siblings of both builders

`YesIntentHandler.PlayAlbum` (YesIntentHandler.cs:312-323): for a confirmed `MusicAlbum` it hand-keeps the
album-tracks initializer (`ParentId + Recursive + MediaTypes=Audio + DtoOptions(true) + AlbumTrackOrder`), builds
`session.NowPlayingQueue`, and mints the whole-album CONCAT URL (`collectionParent = album.Id`, line ~349); that is
the very endpoint JF-763 just folded. Verified costs today:

1. NO JF-338 AlbumIds retry: confirming a disambiguation on a split/malformed album answers `NoSongsInAlbum`
   while the direct ask (AlbumPlayService, which retries) plays it.
2. Queue rows come from `MediaTypes=Audio` while the concat endpoint enumerates `IncludeItemTypes=Audio` (two
   field sets feeding the same JF-625 seek-bar timeline, the exact drift pair the endpoint fold-in eliminated).
3. NO `ApplyLibraryFilter`, the same scope hole JF-763 closed at the head (the JF-666 parity rule).

Righter change (the JF-763 endpoint shape transfers directly): route the MusicAlbum case through
`QueueContinuationFetcher.BuildAlbumTracksQuery` (plus ApplyLibraryFilter and the JF-338 retry) keeping the JF-361
audiobook leg local (the same ternary the endpoint now uses), or record the accepted boundary in
`BuildAlbumTracksQuery`'s doc the way the endpoint boundary used to be recorded.

`YesIntentHandler.PlayBook` (YesIntentHandler.cs ~358-368), the audiobook twin (low): hand-keeps the chapters shape
(`ParentId + MediaTypes=Audio + DtoOptions(true) + Limit`) instead of
`QueueContinuationFetcher.BuildAudiobookChaptersQuery` (identical field set modulo paging) and also skips the scope
filter. Pre-existing since JF-670.

Note on `PlayAlbumIntentHandler.BuildTrackCountQuery` (no standalone action): it stays declined as a SHAPE
(Limit=0 count-only, CheapDtoOptions, no OrderBy; folding it would add an ORDER BY to a COUNT query, pure cost),
but it also carries no library scope, so a restricted user's album ranking ("un disco di X",
PickMostTracksRelease) can count tag-linked tracks in excluded libraries the scoped play path never serves. If the
method is ever touched, add ApplyLibraryFilter at its call site.

## Finding B (moderate): the user-less concat endpoint vs the scoped paged path shifts the seek-mode resume slice for restricted users

The JF-763 pair of decisions left an internal inconsistency on the VIDEO route for one user class. Decision 1 kept
the concat endpoint user-less BY DESIGN (no session user on the token-gated HTTP path), so it enumerates the
album UNSCOPED; Decision 2 scoped the head's track pages (JF-666 tail parity). Consequence, verified in source:
`AlbumPlayService.BuildAlbumPlayResponseAsync` computes the seek-mode resume offset as
`albumItems.Take(startIndex).Sum(RunTimeTicks)` plus the in-track partial (AlbumPlayService.cs:689-692) over the
now-SCOPED row set and passes it as the concat URL's `?start=` slice (the sliced-playlist mechanism), while the
AudiobookPositionTracker records positions from segment fetches on the UNSCOPED concat timeline. For a
library-restricted user on a JF-338 split album whose AlbumIds membership spans excluded libraries, the prefix
sums differ by the runtime of the excluded tracks, so the resume slice lands at the wrong absolute position. Pre
JF-763 both sides were unscoped and this video-route resume was internally consistent (while playing
excluded-library content, the policy hole Decision 2 closes on the audio path, which is the default route; seek
mode is opt-in per user via GetVideoAppForAudio, and VideoApp emits no continuation events, so the tail never runs
there).

Root fix (recorded on `BuildAlbumTracksQuery`'s doc as the accepted residual): thread the user's library scope
into the concat enumeration by extending the JF-309 item-scoped token (mint the resolved TopParentIds into the
HMAC payload at launch, let the endpoint apply them), which also closes the video route's queue-superset policy
hole. An intermediate option considered and declined in the JF-763 round: conditioning the head's filter on the
route (scoped only when not launching the concat) would keep the video queue enumerating excluded-library
content, a special case that preserves the policy hole to dodge the residual.

Decision needed: implement the token-scope threading (Finding B root fix), and fold or document the Finding A
siblings.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Finding A decided: YesIntentHandler.PlayAlbum's MusicAlbum case routed through the builder (with scope +
      JF-338 retry, audiobook leg local) and PlayBook routed through BuildAudiobookChaptersQuery, or both recorded
      as accepted boundaries in the two builders' docs
- [ ] #2 Finding B decided: the JF-309 token carries the user's resolved library scope and the concat endpoint
      applies it (with the seek-mode resume walk and the concat row set back on one timeline), or the residual
      re-verified and re-documented at its new home
- [ ] #3 Any behavior change ships with red proofs (the JF-763 sabotage convention) and the suite green both TFMs
<!-- DOD:END -->
