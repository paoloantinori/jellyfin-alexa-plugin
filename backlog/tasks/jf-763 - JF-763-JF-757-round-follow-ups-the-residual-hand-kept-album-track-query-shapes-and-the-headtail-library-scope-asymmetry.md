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
- [ ] #1 Decision 1 recorded: fold-in (with the unpaged-form design) or accepted boundary (endpoint comment
      strengthened to name the builder as the canonical shape)
- [ ] #2 Decision 2 recorded: head track pages scoped (ApplyLibraryFilter at both head arms, tail parity, with the
      builder-doc remark and lockstep pin updated) or asymmetry documented as accepted with the reason
- [ ] #3 If any fold-in ships: suite green both TFMs and a sabotage of the builder's shared fields reds an
      endpoint/head-side pin
<!-- DOD:END -->
