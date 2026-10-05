---
id: JF-763
title: >-
  JF-763 - JF-757 round follow-ups: the residual hand-kept album-track query
  shapes, and the head/tail library-scope asymmetry
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-05 02:23'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-757 -
    JF-757-the-album-track-query-shape-is-hand-kept-in-four-copies-head-and-tail-x-ParentId-and-AlbumIds-a-BuildAlbumTracksQuery-builder-would-own-it.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-757 /simplify + /code-review passes (2026-10-04, findings deduped into this one umbrella): the
JF-757 builder (`QueueContinuationFetcher.BuildAlbumTracksQuery`) consolidated the FOUR paged-playback initializers
(album head primary + JF-338 AlbumIds retry, album tail primary + retry). Two decision items and a complete
residual inventory came out of the review round.

## DECISION 1: the album-concat endpoint's two hand-kept arms (fold in vs accepted boundary)

`VideoAudioController.cs` carries a FIFTH and SIXTH hand-kept copy of the same shape:

1. ~2990-2996, the isMusicAlbum ParentId arm (the non-album path has a polymorphic Audio/AudioBook kind switch)
2. ~3013-3024, the isMusicAlbum AlbumIds arm (the JF-338 mirror; field-for-field the builder's membership arm:
   Audio kind, Recursive, DtoOptions(true), AlbumTrackOrder, AlbumIds=[guid])

Both share 5 of the builder's 6 load-bearing fields and diverge only in User (absent: token-gated HTTP endpoint, no
session user) and paging (none; GetItemList fetches all). The endpoint's own comment names the contract:
AlbumPlayService sums the resume offset against this order and the concat timeline encodes in it, so a second copy
is the drift risk the AlbumTrackOrder constant exists to kill (wrong-track resume slices). Today only the ORDER
constant is shared; the arms, kind filter, and DTO options are hand-kept, so a future shape evolution landed in the
builder silently bypasses the endpoint.

- (a) accept the boundary as-is (the builder doc already names it since JF-757), or
- (b) grow `BuildAlbumTracksQuery` an unpaged form (OPTIONAL paging; `Limit=0` is Take(0) semantics per the JF-443
  count-query finding, so "unpaged" needs real optionality, not zero) and route both isMusicAlbum arms through it
  with `jellyfinUser: null`. Behavior-sensitive: assigning `User = null` explicitly is byte-identical to today's
  unset (reference type, default null), but the endpoint deliberately carries NO session user, and the ParentId arm
  is NOT a drop-in (the polymorphic Audio/AudioBook kind switch stays endpoint-local or becomes a builder param).

## DECISION 2: the head's track pages carry NO per-user library filter while the tail's do

Pre-existing asymmetry surfaced by the JF-757 lockstep pin's charter (head-vs-tail identical modulo paging): the
TAIL arms apply `Util.LibraryFilter.ApplyLibraryFilter` (QueueContinuationFetcher.cs:239 primary, :277 AlbumIds
retry; JF-666: "without it a continuation batch widens the scope to every library the Jellyfin account sees"),
while the HEAD's track pages (both arms, AlbumPlayService.BuildAlbumPlayResponseAsync) never did - the head applies
the filter only to the album SEARCH query (AlbumPlayService.cs:339, inside BuildAlbumQuery) and the playlist query
(:828). For a restricted-library user the head's first page and the tail's continuation batches can therefore
enumerate different row sets (head total vs tail pages mismatch: tracks skipped or duplicated mid-album). Decide:
scope the head's track pages too (ApplyLibraryFilter at both head arms, mirroring the tail), or document the
asymmetry as accepted. The JF-757 builder doc's returns remark already records that library filtering differs by
site; if the decision is "filter the head", that remark and the lockstep pin (which exempts the TopParentIds
dimension) both need updating in the same change.

## Residual inventory (reviewed this round; NO action unless Decision 1 broadens scope)

- `PlayRandomIntentHandler.ExpandAlbumToTracks` (~249-262): folder arm, NO OrderBy (shuffle by design), fetch-all.
  Declined for the builder (unwanted DB sort + synthetic limit); accepted hand-kept.
- `PlayAlbumIntentHandler.BuildTrackCountQuery` (~821): count-only (Limit=0, CheapDtoOptions, no OrderBy); not a
  merge candidate. NAMING NIT: its arm parameter `byParentId` (true = folder arm) vs the builder's `byAlbumIds`
  (true = membership arm) - inverted polarity for the same conceptual choice; rename one if touched (no test pins
  the flag polarity at call sites; both families pass it named, which is the only guard today).
- `YesIntentHandler.PlayAlbum` (~312-323): whole-album fetch with deliberately MediaTypes=Audio (JF-361: AudioBook
  chapters via PlayBook disambiguation), fetch-all, no continuation. Declined: different kind discipline by design.
- `AplUserEventHandler` (~244-253): album-folder tap first-child resolver, MediaTypes, Limit 500, no continuation.
  Declined: same MediaTypes-by-design class, not a playback page.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Decision 1 recorded: FOLD IN (see Resolutions below). Both VideoAudioController isMusicAlbum arms route
      through the new unpaged/user-less builder entry point (one private field-set core shared with the paged
      form); the accepted-boundary option was declined on the endpoint's own drift-risk evidence
- [x] #2 Decision 2 recorded: HEAD SCOPED (see Resolutions below). ApplyLibraryFilter at both head arms, tail
      parity; the builder-doc returns remark rewritten and the JF-757 lockstep pin joined the TopParentIds
      dimension (restricted user threaded at both ends, absolute id + head/tail equality asserted); the review
      residual (seek-mode concat timeline is user-less) verified, documented at both sites, and filed as JF-767
      Finding B
- [x] #3 Fold-in shipped with proofs: sabotage of the builder core's shared kind field (IncludeItemTypes flipped
      to AudioBook) redded the new endpoint pin StreamHlsAudiobook_AlbumParent_BothArms_RouteThroughTheAlbumTracks
      BuilderUnpaged on both TFMs; sabotage of Decision 2 (head filter removed) redded the lockstep pin at the
      Contains(musicLibId) assert on both TFMs; suite counts in the Final Summary
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both decisions SHIPPED as code, not just recorded. Decision 1 (fold-in): `BuildAlbumTracksQueryUnpaged` over a
private `BuildAlbumTracksQueryCore` (the ONE field-set owner; paged signature stays typed int/int); both endpoint
isMusicAlbum arms route through it, the audiobook leg keeps its local AudioBook initializer via a ternary;
byte-identical semantics (int? paging null = fetch-all, JF-443; User null = unset). Decision 2 (scope the head):
ApplyLibraryFilter at both head arms mirroring the tail's structure; row-neutral for unrestricted users and normal
folder albums (the tail's production-proven shape). The residual inventory's four adjudications re-verified on
current main, all hold; the two new endpoint pins and the lockstep pin's new TopParentIds dimension carry the
sabotage red-proofs (builder-kind flip reds the endpoint pin; head-filter removal reds the lockstep pin, both
TFMs). Gates: worker /simplify (4 angles; applied the core's nullable direct-assignment, the comment dedup, the
WriteFlushLagFakeFfmpeg extraction at the third copy, AssertNoMediaTypesFilter + redundant-NotNull drop; declined
the scoped build+filter helper, below the third-copy threshold; efficiency CLEAN verified at the exact server
tags) + /code-review high (4 findings: the seek-mode resume residual verified in source and filed as JF-767
Finding B with the doc claims corrected at both sites; the endpoint pin's terminal assert strengthened to
IsType<PhysicalFileResult>; the banned hyphen forms rewritten; the Resolutions block moved outside the
description markers). Suites: 5207/5207 net9.0 AND net10.0 (main baseline 5205 + exactly 2 new Facts); Release
build 0 warnings 0 errors with -warnaserror; validators PASS at baseline. Out-of-scope findings filed same-turn as
JF-767 (Finding A: the YesIntent confirmation-path siblings of both builders; Finding B: the user-less concat
endpoint vs the scoped paged path, root fix = thread the scope through the JF-309 token). No deploy: rides the
wave deploy with the batch.

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker commit 0a94175b + orchestrator tail 2be72820 with the structural pairing helper, --no-ff; the gate-marker's six axes PASS with the sabotages independently re-run), deployed in the wave's batched deploy. JF-767 filed by this task.
<!-- SECTION:FINAL_SUMMARY:END -->
