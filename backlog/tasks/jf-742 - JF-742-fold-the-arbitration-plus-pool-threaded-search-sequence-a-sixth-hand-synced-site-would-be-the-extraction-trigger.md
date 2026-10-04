---
id: JF-742
title: >-
  JF-742 - fold the arbitration-plus-pool-threaded-search sequence (a sixth
  hand-synced site would be the extraction trigger)
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - dedup
dependencies:
  - JF-734
references:
  - >-
    backlog/tasks/jf-734 - PlayArtistSongs-pool-threading-completion-and-the-deeper-view-memo-for-the-JF-715-preloadedPool-axis.md
  - >-
    backlog/tasks/jf-715 - JF-715-extract-the-multi-value-ER-gate-consume-composite-one-shared-shape-for-the-three-songmusician-handlers-and-thread-the-arbitration-pool-through-SearchAsyncs-fall-through-legs.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn by the JF-734 worker (reserved number), satisfying
the review-recommendation discipline: the JF-734 code-review high round flagged
a real structural finding whose fix spans handlers outside JF-734's enumerated
surface (PlayArtistSongsIntentHandler, ArtistSearch/SearchService, and their
tests), so it is skipped with a reason and filed here instead.

FINDING (code-review high, ranked 3 of 3): the "pin view -> TryArbitrate ->
ask-return -> survivor-adopt -> else SearchAsync(preloadedPool: pool)"
sequence is now hand-synced at FIVE pool-threading sites: the JF-715 composite
(MultiValueErDisambiguation.TryArbitrateOrSearchAsync, serving PlaySong,
AddToQueue, PlayNext), PlayArtistSongsIntentHandler (~235-276, threaded by
JF-734), QueryArtistLibraryIntentHandler (~138-159, JF-715), and
FindSongIntentHandler's two musician-supplied legs (~250-290, ~356-384,
JF-715). PlayAlbumIntentHandler is a sixth POOL CONSUMER in a deliberately
divergent shape (the arbitration is conditional on the empty-album branch and
feeds local state; the search sits in a separate block with its own JF-492
fallback), counted as a consumer, not a copy. The cost of the status quo: the
next SearchAsync axis or arbitration-contract change must be hand-applied to
five sites, and a missed one compiles clean and diverges silently, the exact
class JF-715 itself was filed to fix (its trigger was the same sequence at
three sites).

COUNTERWEIGHTS a fix must weigh (recorded by the JF-734 rounds, both reviews):
the existing composite structurally cannot host the artist-only twins without
growing four axes (it pins internally and returns no view, while
PlayArtistSongs needs the pinned view after the search for the JF-420 gate,
the Fast-mode fuzzy pick, and the JF-652 pool; it hardcodes default policy
axes while PlayArtistSongs passes mode/asrCompoundWordFixEnabled/
parallelDbTiers; it collapses results to List<Guid> plus one name while the
artist-only handlers consume IReadOnlyList<BaseItem> through judgment gates;
it hardcodes the NotFoundSongByArtist terminal while each twin owns a
different tail). A fold is therefore an ARTIST-ONLY composite (a sibling of
TryArbitrateOrSearchAsync, parameterized on the policy axes and the retry
label, returning the pinned view plus the artists list, tail left at the
handler), not an extension of the existing one. The JF-734 altitude review
argued even that sibling risks a parameter-swamped shape; the JF-734
code-review argued the five hand-synced copies are worse. This task exists to
settle that with the sites' actual variance on the table.

ACCEPTANCE SHAPE: either the artist-only composite lands (with the counting
pin family extended: each folded site's GetArtistsCalls==1 pin moves to drive
the composite), or the task closes with a documented decline naming the axis
variance that makes the sibling a net loss. Do NOT fold PlayAlbum (its
conditional-arbitration shape is the documented divergence, not an omission).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Decision recorded: artist-only composite extracted, or declined with the named axis-variance reason
- [ ] #2 If extracted: all four direct sites folded (PlayArtistSongs, QueryArtistLibrary, FindSong x2), pins green, no behavioral delta; if declined: this file carries the reason
- [ ] #3 dotnet test passes both TFMs
- [ ] #4 /simplify + /code-review high passed
<!-- DOD:END -->
