---
id: JF-797
title: >-
  JF-797 - the PlayBook normalization hardening residuals: the payload-kind
  gate, the lazy folder resolution, and the fresh-ask resume discriminator
status: To Do
  JF-797 - the PlayBook normalization hardening residuals: the payload-kind gate, the
  lazy folder resolution, and the fresh-ask resume discriminator
assignee: []
created_date: '2026-10-06'
updated_date: '2026-10-07 08:41'
labels:
  - tech-debt
  - audiobooks
milestone: m-18
dependencies:
  - JF-793
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookItems.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-793 gate-marker (2026-10-06), findings 2, 5, and 6, same-turn per the
review-recommendation rule. Three hardening items on the normalization path JF-793 built,
none a live defect today:

1. THE PAYLOAD-KIND GATE (marker F2): AudiobookItems.IsBookDisambiguationPayload routes
   ANY non-MusicAlbum Folder carrying the MediaTypeAlbum disambiguation label to the
   PlayBook leg. The two live producers happen to emit AudioBook leaves and MusicAlbums,
   so the breadth is latent; but any current-or-future producer emitting a MusicArtist,
   MusicGenre, CollectionFolder, or playlist folder would reach the PlayBook leg, whose
   TryResolveBookFolder answers null and whose chapters query on the folder returns zero
   children, playing the folder through the single-file fallback: a broken launch where
   the pre-JF-793 album leg handled it. Fix shape: a kind deny-list (or a positive
   children-are-AudioBook probe) narrowing the payload gate to the two intended shapes,
   with a pin per denied kind.

2. THE LAZY FOLDER RESOLUTION (marker F5): NormalizeBookCandidates issues one
   GetItemById per search candidate on EVERY multi-candidate ask, before the fuzzy
   consumers narrow to a >=90 auto-play pick or a Take(3) prompt. A short title whose
   SearchTerm returns dozens of rows pays N folder fetches whose results mostly
   evaporate. Fix shape: dedup by ParentId first and resolve folders only for the
   surviving ids, or resolve lazily for the top-scored candidates.

3. THE FRESH-ASK RESUME DISCRIMINATOR (marker F6): the finding-4 deep-resume guard keys
   on the page-1 scan's (0,0) answer, which an unstarted multi-page book also produces,
   so every FIRST-EVER ask of a long book runs the unpaged recursive fetch and finds
   nothing (one bounded query on the hot fresh-play path inside the Alexa window; the
   trade is now stated in the block comment). Fix shape: a cheap any-Played/positioned
   flag check ahead of the fetch (or folding the discriminator into the head query).
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

DoD notes (2026-10-07): #1/#3 verified by the Release build on the final tree,
0 warnings 0 errors both TFMs (TreatWarningsAsErrors is on). #2 the full suite
at the final state: 5484/5484 net9.0 AND net10.0 (baseline 5466 plus 18 new
test cases). #4 N/A: no session attributes touched (constraint honored; the
discriminator, the gate, and the deny-list are query/store level). #5 N/A: no
HttpClient changes. #6 N/A: no interaction-model change (constraint honored).
#7 N/A at the E2E level with the unit tier covered: the handler logic is pinned
by 18 new unit cases (the masking pair, the fresh-skip query-count pair, the
positioned-entry valve pin, the twelve-kind payload Theory, the books-disabled
interaction pin, the folder-fetch count pin); the E2E suite needs a live
Jellyfin and the worktree does not deploy (the JF-808 convention). #8 N/A: no
user-facing string changed (the NoSongsInAlbum and NoContentInBook pins read
the existing strings). #9 /simplify: 4 review agents (reuse, simplification,
efficiency, altitude), 6 findings applied, 3 skips recorded in the commit.
#10 /code-review high: 5 findings, F1/F2/F4/F5 applied, F3 documented in the
code and filed as JF-812 same-turn.

## Final Summary

Landed in six commits (c8404ca9 red proofs, c0d48cab items 1+2, 86b5b3bb item 3,
01f20b80 item 4, 188728b1 /simplify round, f6adaf8e code-review round).

ITEM 1, the masking shape (landed SECOND per the task's priority order, with
the red proofs FIRST): both deep-resume gates (AudiobookPlayResolver.PlayBookAsync
and AlbumPlayService.BuildAlbumPlayResponseAsync) drop the startIndex == 0
condition; the gate now fires whenever the page scan answers ticks == 0 and
more pages remain. The album path READS the ticks it used to discard
(resumePosition: true, which never affects index selection) so both twins key
on the same signal. RED PROOFS on the unmodified tree, both TFMs, the exact
task shape (a played prefix of 3 on page 1 plus an in-progress track at track
10 beyond the page, 26 rows): both pins launched the shallow prefix successor
(track 4) where track 10 was expected; after the fix both launch track 10 at
its position with the continuation rebased to index 14 against the real total.

ITEM 2, the fresh-ask discriminator: the unpaged deep fetch is now gated by
QueueContinuationFetcher.MayHaveResumeRelevantUserDataAsync, the ONE shared
helper both twins call: two bounded probe queries (IsPlayed then IsResumable,
Limit 1, cheap DtoOptions, each its own fresh query built through the caller's
scoped page builder) short-circuiting on the first hit. The server semantics
were source-read at the pinned tags (v10.11.8 BaseItemRepository, v12.0
BaseItemRepository.TranslateQuery): IsResumable filters the user's RAW
PlaybackPositionTicks > 0 with no MinResumePosition threshold, exactly
FindResumeTrackIndex's in-progress predicate, so the probe can only over-fire;
a miss proves no row anywhere carries resume-relevant user data. The probes
cover the whole collection because a flagged query's server-side filter runs
before paging. The book twin layers one JF-581 safety valve beside the probes
(DeviceQueueManager.HasAnyStoredPosition, the new locked aggregate read): the
device position store can hold the ONLY surviving progress after a server-side
UserData write loss. The code-review round established the valve's production
cost honestly (the store is written by every qualifying stop, so played
households keep paying the book fetch; the album twin, the row-volume case the
JF-796 addendum named, has no valve and discriminates unconditionally) and the
store-kind stamping is filed as JF-812. Pins: the fresh multi-page book and
album asks run NO unpaged query and exactly one probe per flag; the deep asks
still fetch (the existing deep-resume pins plus the unpaged-count assertions);
the JF-581 valve pin proves a positioned entry still releases the fetch; the
JF-757 lockstep pin extended to the seven-query era with the probes' field-set
block.

ITEM 3, the payload-kind gate: IsBookDisambiguationPayload's Folder arm is
narrowed by a kind deny-list made EXHAUSTIVE over the controller refs'
concrete Folder subclasses (reflection-enumerated, byte-identical sets at
10.11.8 and 12.0.0; MusicGenre stays as the documented belt-and-braces entry),
so only plain Folders (the book-folder payload) and AudioBooks reach the
PlayBook leg; every denied kind falls through to YesIntentHandler's album-leg
switch arm, the pre-JF-793 shape (NoSongsInAlbum for a childless payload,
verified: none of the denied kinds implements IHasMediaSources). The
positive-probe alternative was evaluated and declined in the doc. Pins: a
twelve-kind Theory asserting the album-leg fallback plus the books-disabled
interaction pin (a denied payload with books off answers the album leg, not
FeatureDisabled, closing the JF-795 addendum's observable effect).

ITEM 4, the lazy folder resolution: NormalizeBookCandidates drives the batched
TryResolveBookFolder overload, which threads a per-ask ParentId scratchpad into
the ONE verified climb seam (the cache-aware TryResolveVerifiedParentFolder
overload; the fail-closed contract stayed one body after the /simplify round),
so the GetItemById parent fetch runs once per DISTINCT book folder. Pin: 9
chapter leaves of 3 books cost exactly 3 GetItemById calls (red at 9 pre-fix)
with the prompt still book-granular.

Gates: /simplify (4 agents, 6 applied, 3 skips recorded) then /code-review
high (5 findings: the unlocked ItemPositionState enumeration fixed under
_launchScopeLock, the deny-list made exhaustive, the doc-honesty fixes, the
gate debug logs, F3 documented and filed as JF-812). Verification battery:
touched classes green on both TFMs at every step, the full suite 5484/5484 on
both TFMs at the final state, Release -warnaserror 0 warnings 0 errors. No
locale, model, speech, or session-attribute changes. Production surface
changed: deploys on merge.

JF-795 GATE-MARKER ADDENDA (2026-10-06, same-turn): (a) item 3's exposure DOUBLED - the deep-resume unpaged fetch on every first-ever multi-page ask now fires on BOTH the direct ask and the YesIntent confirm (the shared AudiobookPlayResolver.PlayBookAsync), making the cheap discriminator more valuable; (b) item 1's payload-kind breadth now has a wider observable effect - the JF-795 BooksEnabled confirm gate sits inside the same over-broad IsBookDisambiguationPayload, so with books disabled a MediaTypeAlbum-labeled confirm carrying an arbitrary non-album Folder answers the FeatureDisabled Tell instead of reaching the album leg (the gate is right for real books; the breadth is the root, unchanged).

JF-796 ADDENDUM (2026-10-07, same-turn): item 3 now has a THIRD caller surface and a
sharpened scope. The album head's deep-resume block (AlbumPlayService) adopted the same
(0,0)-keyed guard, so the unpaged first-ever-ask fetch fires on multi-page ALBUMS too.
Albums are not books on volume: compilations and box sets with hundreds of tracks are
normal music-library shapes (books carry bounded chapter counts), the deep query
materializes every row with full-field DtoOptions(true), and FindResumeTrackIndex then
issues one GetUserData per track, all inside the Alexa ~8s window; only the query COUNT
is bounded, not the row count. The discriminator design should therefore evaluate a row
bound or an IsPlayed/position-prefiltered variant (the server-side filter the album
shape could carry), not just the any-Played flag check the book head needs. The lift of
the algorithm itself is filed separately as JF-803; implement the discriminator once
inside the lifted helper.

JF-796 GATE-MARKER ADDENDUM (2026-10-07): item 3 gains the MASKING SHAPE - the deep-resolution gate keys on startIndex == 0, so a PLAYED PREFIX on page 1 (after-last-played at index > 0, ticks 0) suppresses the deep scan even when a deeper in-progress track exists beyond the page; resume lands at the shallow prefix position instead of the full-list answer. The JF-793 book twin's guard in AudiobookPlayResolver shares the shape, so the fix (deep-scan whenever the page yields ticks == 0 AND more pages remain, regardless of the prefix index) must land mirrored on both paths with pins on both. The JF-796 block comment documents only the unstarted-album trade; extend it when fixing.
