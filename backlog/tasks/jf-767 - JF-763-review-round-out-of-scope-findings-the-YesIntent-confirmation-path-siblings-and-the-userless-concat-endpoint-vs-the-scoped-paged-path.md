---
id: JF-767
title: >-
  JF-767 - JF-763 review-round out-of-scope findings: the YesIntent confirmation-path
  siblings, and the user-less concat endpoint vs the scoped paged path
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-06'
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
- [x] #1 Finding A decided: FOLDED BOTH SIBLINGS. PlayAlbum's MusicAlbum case routes through
      BuildScopedAlbumTracksQueryUnpaged (the new unpaged+scoped builder sibling: session user,
      JF-666 scope, JF-338 AlbumIds retry) with the non-MusicAlbum leg local (the endpoint's
      ternary shape, MediaTypes=Audio kept for the JF-361 AudioBook-chapter kind discipline,
      scope applied at the call site after code-review F5); PlayBook routes through the new
      BuildScopedAudiobookChaptersQuery sibling (which FetchAudiobookChapters and, after
      code-review F3, PlayBookIntentHandler's head also run, so head/confirm/tail share ONE
      scoped pairing). BuildTrackCountQuery stays declined as a shape (the JF-763 adjudication;
      the no-standalone-action note in the description stands)
- [x] #2 Finding B decided: the token threading IMPLEMENTED (three-field scoped form
      {expires}.{scope}.{hmac}, HMAC covering the scope; unrestricted users mint the legacy
      two-field form byte-identically; the endpoint resolves the token's raw AllowedLibraryIds
      once per request with the paged path's own resolver and applies TopParentIds to both
      arms). Deliberate deviation from the task letter ("mint the resolved TopParentIds"):
      the URL builder holds no ILibraryManager and serve-time resolution keeps the token in
      step with current queries (documented on GetAudiobookResumeUrl with the launch-to-fetch
      window the paged path already shares). The residual RE-VERIFIED at its new home: the
      scope governs the live enumeration only, never the shared concat cache (keyed by
      parentId+artModifiedTicks), documented at the endpoint comment and FILED as JF-784
      with the second code-review residual (the unbounded scope rendering)
- [x] #3 Red proofs: pre-fix RED on the unmodified tree for the Finding A pins (split-album
      confirm: Expected 2 queries / Actual 1, no retry; PlayBook pin: Expected StartIndex 0 /
      Actual null), both TFMs; post-fix sabotage REDs: endpoint scope application removed (the
      scoped-token pin: Expected [musicLib] / Actual []), mint scope nulled (the three
      builder-side scope pins), PlayBook head reverted to the unscoped builder (the extended
      head pin). Suites 5335/5335 net9.0 AND net10.0 (main baseline 5314 + 21 new Facts);
      Release -warnaserror clean
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both findings shipped as code. FINDING A (the confirmation-path siblings): PlayAlbum's
MusicAlbum case runs the paged head's exact triple (the ONE builder unpaged with the session
user via the new BuildScopedAlbumTracksQueryUnpaged, the JF-666 scope, the JF-338 AlbumIds
retry), so a confirmed split album now PLAYS instead of answering NoSongsInAlbum and the
queue rows feed the concat timeline the same field set the endpoint encodes; the
non-MusicAlbum leg stays local (the JF-361 kind discipline) and gained the scope filter
(code-review F5); PlayBook runs the new BuildScopedAudiobookChaptersQuery sibling, and the
review round extended the structural pairing to the tail (rerouted) and PlayBookIntentHandler's
head (code-review F3, with the head pin's new scope assert), so head, confirm, and tail share
one scoped chapters pairing. PlayAlbum also adopted the JF-699 launch-before-writes ordering
(code-review F2, the MusicAlbum leg mints the token-gated concat URL). FINDING B (the root
fix): StreamTokenHelper gained the scoped wire form (canonical sorted/deduped "N" GUIDs in the
HMAC payload; MintScoped, the scope-reading TryValidate, both over one private mint core and
one validation core that never parses the scope on the discarding per-segment paths);
GetAudiobookResumeUrl mints the launching user's AllowedLibraryIds (raw ids, serve-time
resolution: the documented deviation with its window rationale), and StreamHlsAudiobook
resolves them once per request through LibraryFilter's own resolver and applies TopParentIds
to both isMusicAlbum arms and the audiobook arm, so the encoded timeline is the timeline the
scoped head sums the seek-mode resume offset over; legacy/unrestricted tokens enumerate
byte-identically to before. OUT OF SCOPE, FILED AS JF-784: the concat cache's scope blindness
(a cache entry encoded under a different scope serves unchanged to a scoped request; both fix
shapes sketched) and the unbounded scope rendering (bound documented on RenderScope). Gates:
worker /simplify (4 angles; applied the scoped chapters sibling killing the mixed pairing
convention, the mint-core and validation-core consolidations with the hot-path parse removed,
the endpoint's once-per-request scope resolution hoist, and four test-helper consolidations;
declined the hand-pair alternative to the scoped builders and the retry-owning enumerator
(the executors-stay-at-callers contract, divergent empty-tails)). /code-review high (6
findings: F2/F3/F5 applied with the head pin's scope assert, F6 applied as the doc sentence on
the resolution window; F1 and F4 filed as JF-784 with doc notes at both sites). Red proofs:
pre-fix RED for the Finding A pins on the unmodified tree, plus three post-fix sabotage REDs
(endpoint scope, mint scope, head scope), all both TFMs. Suites: 5335/5335 both TFMs (5314
baseline + 21 new Facts); Release --no-restore -warnaserror clean. Not deployed (worker
branch; rides the wave deploy).
<!-- SECTION:FINAL_SUMMARY:END -->
