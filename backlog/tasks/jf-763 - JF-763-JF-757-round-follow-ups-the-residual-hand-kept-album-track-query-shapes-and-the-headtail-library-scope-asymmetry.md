---
id: JF-763
title: >-
  JF-763 - JF-757 round follow-ups: the residual hand-kept album-track query
  shapes, and the head/tail library-scope asymmetry
status: To Do
assignee: []
created_date: '2026-10-04'
labels: []
references:
  - backlog/tasks/jf-757 - JF-757-the-album-track-query-shape-is-hand-kept-in-four-copies-head-and-tail-x-ParentId-and-AlbumIds-a-BuildAlbumTracksQuery-builder-would-own-it.md
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

## Resolutions (2026-10-05, this task; OUTSIDE the description markers so a Backlog MCP description rewrite cannot clobber the record)

**Decision 1: FOLD IN.** Both `VideoAudioController` isMusicAlbum arms now route through the ONE builder via a new
unpaged, user-less entry point `QueueContinuationFetcher.BuildAlbumTracksQueryUnpaged(albumId, byAlbumIds)`, which
shares a private `BuildAlbumTracksQueryCore` with the paged `BuildAlbumTracksQuery` (the ONE field-set owner; the
paged signature stays typed `int`/`int` so the head/tail paged contract cannot silently degrade to unpaged).
Evidence for fold-in over accepted-boundary: the endpoint's own pre-existing comment names the drift risk
(AlbumPlayService sums the resume offset against `AlbumTrackOrder` and the concat timeline encodes in it, so the
encoded ROW SET must equal the paged queue's row set; that equality was hand-kept on 5 fields with only the ORDER
constant shared), and the fold-in is behavior-identical because `StartIndex`/`Limit` are `int?` in the SDK (null =
fetch-all, NOT `Limit=0` = Take(0), JF-443; proven at the SDK source level,
Jellyfin 10.11.8 BaseItemRepository.ApplyQueryPaging: Limit=0 is Take(0), null
is no paging, the anchor recorded in BuildTrackCountQuery's doc at
PlayAlbumIntentHandler.cs:812-814; the earlier plugin-side null-tolerance
sites only show our own tolerance, not the SDK's behavior) and `User = null` equals unset for the reference-typed
property. The ParentId arm's polymorphic kind switch stays endpoint-local as a ternary: the audiobook leg (AudioBook
kind, NO AlbumTrackOrder) is a different kind discipline, the same declined class as the inventory below. Pins:
`StreamHlsAudiobook_AlbumParent_BothArms_RouteThroughTheAlbumTracksBuilderUnpaged` (both arms field-for-field incl.
the unpaged invariants) + `StreamHlsAudiobook_AudiobookParent_KeepsLocalAudioBookQuery` (the ternary's other leg).
Sabotage red-proof: flipping the core's IncludeItemTypes to AudioBook redded the endpoint pin on both TFMs.

**Decision 2: SCOPE THE HEAD.** `AlbumPlayService.BuildAlbumPlayResponseAsync` applies
`Util.LibraryFilter.ApplyLibraryFilter` at BOTH head track-page arms (build-then-filter-then-execute, mirroring the
tail's `FetchAlbumTracks` structure exactly; query construction hoisted out of the RetryAsync lambda so the filter
applies once per arm). Evidence: JF-666's shipped rule is "continuation batches run under the same per-user library
scope as the initial fetches", but the album head's track pages sat outside "initial fetches" (only its album search
:339 and playlist query :828 were filtered); the asymmetry is user-visible exactly on the JF-338 AlbumIds retry for
restricted-library users (page 1 can enumerate tag-linked tracks from EXCLUDED libraries that the filtered tail then
never serves: an excluded-library track plays and the tail's continuation switches row sets mid-album). Row-neutral
otherwise: unrestricted users no-op; a restricted user's normal folder album passes all children (the album came
from the filtered search, so its folder children share its top parent; the identical ParentId+TopParentIds shape has
run in production on the tail since JF-666). JF-763 IS the filing for this behavior change (its own DoD #2
enumerates the ship path; a second filing would double-track one change). Builder-doc returns remark rewritten
(head and tail both apply ApplyLibraryFilter; the unpaged endpoint form applies neither); the JF-757 lockstep pin
joined the scope dimension: the plugin user is now RESTRICTED in the pin and AssertArmLockstep asserts
`Contains(musicLibId, head.TopParentIds)` plus head/tail TopParentIds equality. Sabotage red-proof: removing the
head's primary-arm ApplyLibraryFilter redded the lockstep pin at the Contains assert on both TFMs.

REVIEW RESIDUAL (code-review high, 2026-10-05, verified in source and filed as JF-767 Finding B): on the opt-in
seek-mode VIDEO route the decision shifts the resume slice for restricted users, because the head now sums
`albumStartTicks` over SCOPED rows (AlbumPlayService.cs:689-692) while the concat endpoint is user-less BY DESIGN
(Decision 1) and encodes the UNSCOPED timeline the tracker records against; pre-JF-763 both sides were unscoped
(consistent, but playing excluded-library content). The audio route (the default) keeps the full benefit; the
root fix (thread the user scope through the JF-309 token) is filed, and the builder doc plus the endpoint comment
now state the row-set boundary instead of claiming definitional equality.

**Residual inventory re-verified on current main (2026-10-05), all four adjudications HOLD unchanged:**
- `PlayRandomIntentHandler.ExpandAlbumToTracks` (lines 249-262): still the folder arm with NO OrderBy
  (Shuffler.Shuffle consumes the raw set; a builder OrderBy would impose an unwanted DB sort), fetch-all. Declined
  stands.
- `PlayAlbumIntentHandler.BuildTrackCountQuery` (~810): still count-only (Limit=0, CheapDtoOptions, no OrderBy), not
  a merge candidate. The `byParentId`/`byAlbumIds` inverted-polarity naming nit stands un-actioned: neither family
  was touched by the decisions, and both call sites (790, 800) still pass the flag NAMED (the only guard; no test
  pins the polarity).
- `YesIntentHandler.PlayAlbum` (312-323): still whole-album fetch with deliberately `MediaTypes=Audio` (JF-361:
  AudioBook chapters arrive via PlayBook disambiguation; IncludeItemTypes=Audio would drop them), fetch-all, no
  continuation. Declined stands (different kind discipline); the code-review round's deeper finding that the
  MusicAlbum case is foldable anyway is JF-767 Finding A.
- `AplUserEventHandler` (244-253): still the album-folder tap first-child resolver, MediaTypes (same JF-361 class:
  audiobook/artist folders flow here too), Limit 500, not a playback page. Declined stands.

## Final Summary

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
