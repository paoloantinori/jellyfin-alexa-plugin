---
id: JF-784
title: >-
  JF-784 - the concat cache's scope blindness and the unbounded token-scope rendering:
  the two JF-767 code-review residuals
status: To Do
assignee: []
created_date: '2026-10-06'
labels: []
references:
  - backlog/tasks/jf-767 - JF-763-review-round-out-of-scope-findings-the-YesIntent-confirmation-path-siblings-and-the-userless-concat-endpoint-vs-the-scoped-paged-path.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-767 /code-review round (2026-10-06, effort high; every claim verified against
source before filing). Both findings are real but live one layer outside JF-767's surface (the
HLS cache subsystem and the token wire format), where each needs its own design pass and red
proofs.

## Finding 1: the token scope governs the live enumeration only, never the shared concat cache

JF-767 threads the launching user's library scope through the JF-309 token and the concat
endpoint applies it to its enumeration. But the encode cache is keyed by
`(parentId, artModifiedTicks)` with NO scope component, and the cache-hit validation
(`ValidateAudiobookCacheAsync`) only checks segment count >= the (scoped) chapter count. So a
cache entry encoded under a different scope, or no scope, is served unchanged to a scoped
request:

- the shape: an unrestricted user (or a differently-restricted one) plays a JF-338 split album
  whose AlbumIds membership spans libraries A+B; the cache encodes the 15-track timeline. A
  library-A-only user then plays or resumes the same album: the paged head sums `?start=` over
  its 12 scoped rows, the endpoint cache-hits the 15-track playlist (12 <= 15 passes), and the
  resume slice lands at the wrong absolute position while the tracker records on the cached
  timeline. Same reopen for one user whose AllowedLibraryIds change while the cache lives.
- pre-JF-767 the same mismatch existed for EVERY restricted user (the endpoint always
  enumerated unscoped); JF-767 closed the encode-creating request and left the cross-scope
  cache-sharing window.

Fix shapes considered, not chosen (both need their own task-sized design):

1. scope in the cache IDENTITY: fold a scope hash into the encode directory name. Blast
   radius: cache dir naming (`{parentId}_{artModifiedTicks}`), `FindSegmentPath`'s `{GUID}_*`
   scan, the segment routes keyed by parentId, eviction, and the JF-428 pin/eviction contract;
   also multiplies cache entries and re-encode work per distinct scope on a shared album.
2. scope-aware cache VALIDATION: when serving a hit under a token scope, verify the cached
   timeline matches the scoped enumeration (e.g. compare the playlist's EXTINF sum against the
   scoped chapters' runtime sum with a drift tolerance) and treat a mismatch as stale. No key
   change, but a heuristic tolerance and a full re-encode (minutes for a long album/book) on
   every scope switch; two alternating users on one album would re-encode each play.

## Finding 2: the scoped token's wire form is unbounded in the library count

`StreamTokenHelper.RenderScope` embeds one ~33-char "N" GUID per allowed library with no cap.
The token rides every playlist segment line (`RewritePlaylistWithToken`, the prewrite) and
every segment request URL, so a user with hundreds of configured libraries gets a multi-KB
token: a 3000-segment playlist grows by megabytes and segment request lines approach Kestrel's
8KB default `MaxRequestLineSize` (431s on every segment fetch). Household scale
(single-digit libraries) is far inside every limit, which is why this is a bound note rather
than an incident. Fix shapes: compact per-id encoding (base64 of the 16 GUID bytes, 22 chars),
or a server-side scope digest table (bounded token, new state + eviction).

## Where the residuals are documented in source

- `VideoAudioController.StreamHlsAudiobook` (the scope application comment carries the
  Finding 1 RESIDUAL paragraph).
- `StreamTokenHelper.RenderScope` doc (the Finding 2 BOUND paragraph).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Finding 1 decided: the cache identity or validation made scope-aware (with red proofs
      for the cross-scope serve this task describes), or the residual re-adjudicated and the
      reasoning recorded here
- [ ] #2 Finding 2 decided: the scope rendering bounded (compact or digest encoding), or the
      bound re-verified as acceptable at realistic library counts and recorded here
<!-- DOD:END -->

LEG 3 (the JF-767 gate-marker's F1): the endpoint's audiobook arm (IncludeItemTypes=[AudioBook]) and the ONE chapters builder (MediaTypes=Audio) now fed by PlayBook head/confirm/tail diverge on the KIND axis of the same endpoint-vs-paged seam JF-767 closed on the scope axis. A book parent with mixed children (AudioBook chapters plus Audio-typed siblings after a metadata remap) queues all chapters via MediaTypes=Audio but the concat endpoint enumerates only the AudioBook subset; a fully Audio-typed folder yields 0 rows at the endpoint (404) for a book the confirm just launched. Fix shape: align the kind axis (either the endpoint accepts both kinds for the audiobook arm or the builder narrows to AudioBook when the parent is a book) with a mixed-children red proof.