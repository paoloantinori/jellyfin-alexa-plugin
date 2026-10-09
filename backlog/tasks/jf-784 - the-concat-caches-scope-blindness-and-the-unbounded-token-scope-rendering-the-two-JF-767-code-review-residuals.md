---
id: JF-784
title: >-
  JF-784 - the concat cache's scope blindness and the unbounded token-scope rendering:
  the two JF-767 code-review residuals
status: Done
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-06'
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
- [x] #1 Finding 1 decided: the cache identity or validation made scope-aware (with red proofs
      for the cross-scope serve this task describes), or the residual re-adjudicated and the
      reasoning recorded here
      (DONE: VALIDATION, shape 2 exact. The serve-time verdict compares the JF-292
      encode-metadata sidecar against the requesting scope's live enumeration (count +
      duration sum, no tolerance; missing sidecar fail-closed). RED on the unmodified tree:
      the cross-scope pin served the foreign 15-track ContentResult (Expected
      PhysicalFileResult / Actual ContentResult), both TFMs; green post-fix with the
      invalidation log + re-encode. The during-encode windows stay as the documented residual;
      their tracker-write dimension is FILED as JF-787)
- [x] #2 Finding 2 decided: the scope rendering bounded (compact or digest encoding), or the
      bound re-verified as acceptable at realistic library counts and recorded here
      (DONE: COMPACT ENCODING. One 22-char base64url field per library replaces the 33-char
      "N"-hex+comma; same wire shape, same separators, no dual-form parse (JF-767 never
      deployed, so no old-form scoped token exists). RED on the unmodified tree: the wire pin
      read 32-char fields (Expected 22 / Actual 32), both TFMs. The BOUND note on RenderScope
      carries the new arithmetic (~350 libraries to Kestrel's 8192-byte MaxRequestLineSize);
      the digest table stays the tracked shape beyond)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All three legs shipped as code, each with a red proof on the unmodified tree (verified RED
first, then green post-fix, both TFMs). LEG 1 (the concat cache's scope blindness): the
serve-time verdict is now TIMELINE-AWARE through the JF-292 encode-metadata sidecar the
encode already writes: ValidateAudiobookCacheAsync's content hook (renamed verdict helper
TimelineMismatchReason) reads the sidecar beside the cached playlist through the ONE reader
TryReadEncodeTimelineMetadata (shared with the post-exit monitor since review F6) and compares
the encoded (chapterCount, durationTicks) against the CURRENT request's scoped enumeration;
any mismatch, or a missing/unreadable sidecar, is the existing debris verdict (ticks-scoped
cleanup + re-encode under the requesting scope). No key change: dir naming, FindSegmentPath,
segment routes, eviction, and the JF-428 pin contract untouched. Same-scope serves keep
sharing the cache (the no-thrash row pinned); alternating differently-scoped users re-encode
per switch (the accepted validation-family cost, recorded in the design record). The
during-encode windows stay as the documented bounded residual, and their position-tracker
dimension (the foreign serve's segment fetches poison the shared resume key past the window)
is FILED as JF-787. LEG 2 (the unbounded scope rendering): RenderScope/ParseScope swap the
per-library field to the 22-char base64url of the 16 GUID bytes (23 chars with separator, down
from 33), same canonical sort/dedupe, same separators, HMAC unchanged; no dual-form parse
because JF-767 never deployed (verified against git history: its merge is this task's own
dependency merge); the BOUND note on RenderScope carries the new arithmetic and the digest
table stays the tracked shape beyond. LEG 3 (the kind-axis divergence): the endpoint's
audiobook arm routes through the ONE chapters builder's new unpaged form
(BuildAudiobookChaptersQueryUnpaged over a private core, mirroring the JF-763 album pattern),
so the concat encodes the queue's MediaTypes=Audio rows: a fully Audio-typed book launches
instead of 404ing and a mixed folder concats all audio children instead of the AudioBook
subset; the JF-763 ternary pin is superseded to the builder shape. REVIEW-ROUND EXTRAS, all
applied with red proofs where behavioral: F1 the copy-compatibility codec gate now covers the
audiobook arm too (the widening re-exposed the mixed-codec silent-truncation shape; pre-fix
hardcoded copy pinned red), F3 the monitor's completeness row compares segments < chapters
instead of equality (every healthy concat encode used to warn INCOMPLETE; pinned red), F7 the
404 body/log name the audio-children axis instead of the AudioBook kind, F2 the false
"per-segment paths never parse the scope" claim corrected at both doc sites (the 4-arg
scope-reading form runs per segment since JF-767). Gates: /simplify (4 parallel angles;
applied the duration-local single owner across the sidecar write and both Estimate* calls, the
verdict's reason-suffix local, the field-name dedupe later subsumed by the ONE reader, the
stale core-param doc, the EncodeScopeId inline, the test seeding dedupe with the sync
WriteEncodeMetadata twin, the invalidation-pins' shared terminal assert, and the provably-dead
N-form asserts dropped; reasoned skips: the monitor/verdict shared reader initially declined
for the sync/async split then landed via review F6, the arrangement-helper consolidation
(deliberately different mock strategies, one-arrangement-per-cluster convention), the
threeMinutes const, the Snapshot hoist) + /code-review high (7 findings, ALL applied: F1-F3,
F5 the tautological RenderedScope derivation rebuilt from a shuffled input, F6 the ONE reader,
F7 the wording; F4's tracker dimension FILED as JF-787). Suites: 5369/5369 BOTH TFMs on the
final state (the merged-tree baseline 5360 + 9 new Facts); Release --no-restore -warnaserror
0 warnings 0 errors; validators PASS at baseline. CODE/TEST/BACKLOG change, no locale, model,
or speech surface: no model deploy needed. Not deployed (worker branch only).
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

LEG 3 (no DoD box of its own; the evidence): CLOSED by alignment. The endpoint's audiobook
arm routes through the ONE chapters builder's new unpaged form, the queue's MediaTypes=Audio
axis. RED on the unmodified tree, both TFMs: the Audio-typed folder pin hit the filed 404
(Expected ContentResult / Actual NotFoundObjectResult) and the mixed-children pin's concat
list dropped the Audio-typed sibling; plus the review-round F1 follow-up (the copy-compatibility
gate extended to the audiobook arm, its pin red under the pre-fix hardcoded copy) and F3 (the
monitor's completeness equality that warned INCOMPLETE on every healthy concat encode, its pin
red under the pre-fix equality).

LEG 3 (the JF-767 gate-marker's F1): the endpoint's audiobook arm (IncludeItemTypes=[AudioBook]) and the ONE chapters builder (MediaTypes=Audio) now fed by PlayBook head/confirm/tail diverge on the KIND axis of the same endpoint-vs-paged seam JF-767 closed on the scope axis. A book parent with mixed children (AudioBook chapters plus Audio-typed siblings after a metadata remap) queues all chapters via MediaTypes=Audio but the concat endpoint enumerates only the AudioBook subset; a fully Audio-typed folder yields 0 rows at the endpoint (404) for a book the confirm just launched. Fix shape: align the kind axis (either the endpoint accepts both kinds for the audiobook arm or the builder narrows to AudioBook when the parent is a book) with a mixed-children red proof.

## Design record (2026-10-06, decided before implementation)

### LEG 1: scope-aware VALIDATION against the encode-time metadata sidecar (shape 2, exact)

The concat path ALREADY writes a per-encode sidecar at encode start (JF-292):
`encode-metadata.json` in the encode directory records `ExpectedChapterCount` and
`ExpectedDurationTicks`, the count and the `Sum(RunTimeTicks ?? 0)` of the exact (scoped)
enumeration that was encoded. The serve-time validator never read it; only the post-exit
monitor did (logging only). The fix: `ValidateAudiobookCacheAsync` compares that sidecar
against the CURRENT request's live scoped enumeration (same two numbers) and treats any
mismatch, or a missing/unreadable sidecar, as stale (the existing debris-verdict row:
ticks-scoped cleanup + re-encode under the requesting scope).

Why this beats both filed shapes:

- vs shape 1 (scope in the cache IDENTITY): no change to the dir naming
  (`{parentId}_{artModifiedTicks}`), `FindSegmentPath`'s `{GUID}_*` scan, the segment routes
  (keyed by parentId only), eviction, or the JF-428 pin/eviction contract; no per-scope entry
  multiplication on shared albums. The registration/liveness subsystem those names feed was
  just stabilized across JF-676..JF-783; a third key axis through it is disproportionate for
  a household plugin.
- vs the filed shape 2 sketch (EXTINF sum vs scoped runtime sum): NO tolerance heuristic.
  Both compared sides are DB-metadata sums over chapter sets, so equal membership compares
  bit-identical; the sketch's drift tolerance existed only because it compared playlist
  EXTINF (ffmpeg output) against DB runtimes. The 12-vs-15 filed shape fails on BOTH axes.
- discrimination bound (accepted): equal count AND equal duration with different membership
  requires two chapter sets with identical totals to the tick; no scope-relevant shape
  produces it. A scope that does not change membership (both scopes include all the album's
  libraries) KEEPS sharing the cache, where the identity/scope-string shapes would thrash.
- missing metadata (pre-sidecar entries, wiped sidecar): stale, fail-closed; a one-time
  re-encode per cached entry after upgrade, self-healing.

Costs accepted from the validation family: alternating differently-scoped users on one album
re-encode on each switch (minutes for a long book); same-scope households see no change.

RESIDUAL (documented, accepted): the DURING-ENCODE windows still serve a foreign timeline to
a differently-scoped request (the own-live unread row in `ValidateHlsCacheAsync` and the
concurrent-encode prewrite/live guard block both serve whatever encode is running, with no
timeline read). Bounded by the encode duration; requires two differently-scoped users on the
SAME album/book within that window; self-heals at the first completed-cache verdict after the
encode exits. Closing it means threading a timeline gate through the JF-782-hardened
unread-acceptance row AND the prewrite guard: rejected as disproportionate here. ONE DIMENSION
OF IT OUTLIVES THE WINDOW (the code-review round's F4, filed as JF-787): the served foreign
listing's segment fetches still RECORD positions on the shared book/album tracker key against
the foreign timeline, and after the post-encode re-encode under the requesting scope the
stored offset maps onto a different timeline (the resume slice lands at unrelated content
until the next position overwrite); closing that needs a timeline-identity axis in the
position tracker, out of proportion to this task.

### LEG 2: compact per-id scope rendering (the filing's own first fix shape)

`RenderScope`/`ParseScope` swap the per-library field from the 32-char "N" hex to base64url
of the 16 GUID bytes (22 chars): ~31% shorter tokens, still stateless, still canonical
(sorted/deduped), still HMAC-covered over the raw field. The base64url alphabet
(A-Z a-z 0-9 - _) contains neither the field separator '.' nor the scope separator ',', so
the field-count parsing and the token shape are unchanged. No dual-form parse: JF-767 has
not deployed (its merge never landed before this task), so no N-form scoped token exists in
the wild; a hypothetical one fails the scope parse fail-closed everywhere validation reads
the scope (the playlist gate AND every per-segment fetch, which uses the scope-reading
4-arg form since JF-767), so it is the absence of old-form tokens, not a per-segment
exemption, that keeps the cutover clean. The BOUND note on
`RenderScope` is updated to the new arithmetic (~351 libraries to Kestrel's 8192-byte
MaxRequestLineSize vs ~244 with hex, both absurd at household scale); the digest-table
remains the tracked shape beyond that.

### LEG 3: the endpoint adopts the builder's kind axis (MediaTypes=Audio), not the reverse

The audiobook arm's local `IncludeItemTypes=[AudioBook]` initializer is replaced by a new
unpaged/user-less entry point over the ONE chapters builder
(`BuildAudiobookChaptersQueryUnpaged` over a private core, mirroring the JF-763 album
pattern: paged signature stays typed int/int). Direction chosen because the QUEUE
(head/confirm/tail) already plays Audio-typed chapters via MediaTypes=Audio in production
(JF-670): the endpoint must encode what the queue queued. Narrowing the builder to AudioBook
would regress playback of Audio-typed books that work today through the queue and re-open
the divergence in the opposite direction. Pure-AudioBook books enumerate byte-identically
(the builder's field set is ParentId + Recursive + MediaTypes=Audio + DtoOptions(true), no
OrderBy; the endpoint keeps its filename-number chapter sort). The JF-763 ternary pin is
superseded to the builder shape; both red proofs condition the mocked GetItemList on the
query's kind axis (rows only for the axis the server actually constrains): the FULLY
Audio-typed folder reproduces the filed 404 pre-fix, and the MIXED-children folder
reproduces the shorter-than-the-queue subset concat pre-fix (chapters.txt missing the
Audio-typed sibling).
<!-- SECTION:NOTES:END -->
