---
id: JF-667
title: >-
  JF-358 sibling sweep: three ArtistIds+MediaTypes query sites survive outside
  the continuation fetcher (YesIntentHandler.PlayArtist, QueryArtistLibrary
  tracks listing, SearchMedia redundant filter)
status: Done
assignee: []
created_date: '2026-09-29 06:45'
updated_date: '2026-09-29 13:08'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-666 code-review round (2026-09-29), all three sites verified in source.

BACKGROUND (the JF-358 rule, live-proven twice): MediaTypes=Audio does NOT constrain an ArtistIds query. CLAUDE.md's Key Gotchas (JF-358) documents it for Jellyfin 10.11.11 (returns the ENTIRE audio library); JF-666 added direct-path evidence on the live 12.1.0 box: the same artist via IncludeItemTypes=BaseItemKind.Audio returns 13 tracks, while ArtistIds+MediaTypes on the direct ILibraryManager GetItemList path returned ZERO items at StartIndex=5 (silently). The rule: every ArtistIds-filtered query must use IncludeItemTypes=BaseItemKind.Audio and must not carry a MediaTypes term. JF-666 fixed QueueContinuationFetcher.FetchArtistSongs only (the live defect); these sibling sites still carry the broken or redundant shape:

1. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs, PlayArtist (~line 401): query is ArtistIds + MediaTypes=Audio + GetItemList, NO IncludeItemTypes. Failure: confirming an artist disambiguation (or the JF-363 cross-media artist offer) with "yes" on the live 12.1.0 box can hit the zero-return shape and speak NoSongsForArtist for an artist that has tracks.

2. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/QueryArtistLibraryIntentHandler.cs (~line 192): the tracks listing calls ListItemsByArtistAsync with includeItemTypes=null and mediaTypes={Audio} (the albums listing at ~line 170 uses the item-type path correctly). Failure: "what tracks does X have" can answer NoSongsForArtist instead of listing tracks.

3. Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs (~line 515): the artist-fallback query carries BOTH MediaTypes=Audio AND IncludeItemTypes=FilterByContentAccess(_playableTypes) on an ArtistIds query. The redundant MediaTypes term is dead weight at best and the documented-broken combination at worst; drop it so the site depends only on IncludeItemTypes.

SCOPE NOTE: fix the query shapes only; do NOT restructure the handlers. Each site needs its own verification (captured-query test + the existing suite; live spot check where the path is reachable). Also note for triage: greppable via `MediaTypes = new\[\] { MediaType.Audio }` near ArtistIds; SearchService.GetArtistSongsAsync and PlayArtistSongsIntentHandler are already on the IncludeItemTypes shape (do not touch).
<!-- SECTION:DESCRIPTION:END -->

## Notes

<!-- SECTION:NOTES:BEGIN -->
SITE MAP (all four fixed; grep `ArtistIds` across the plugin swept both directions, zero live ArtistIds+MediaTypes combinations remain):

1. YesIntentHandler.PlayArtist (Alexa/Handler/Intent/YesIntentHandler.cs ~:401): `MediaTypes = new[] { MediaType.Audio }` on the ArtistIds query replaced with `IncludeItemTypes = new[] { BaseItemKind.Audio }` + the JF-358 comment. Nothing else in the query changed; the response shapes are untouched.
2. QueryArtistLibraryIntentHandler tracks listing (Alexa/Handler/Intent/QueryArtistLibraryIntentHandler.cs ~:186): the tracks arm now passes `new[] { BaseItemKind.Audio }` as includeItemTypes into ListItemsByArtistAsync (the albums arm's shape, JF-358-aligned). The `MediaType[]? mediaTypes` parameter and its assignment block were removed from the private helper (both call sites in-file; it existed solely to carry the broken shape, and leaving a dead parameter for a removed shape contradicts the no-errata rule). The albums call dropped its `null` argument accordingly. Albums path behavior unchanged.
3. SearchMediaIntentHandler artist fallback (Alexa/Handler/Intent/SearchMediaIntentHandler.cs ~:515): the redundant `MediaTypes = new[] { MediaType.Audio }` term dropped; IncludeItemTypes=FilterByContentAccess(_playableTypes) is the single filtering term on the ArtistIds query.
4. SWEEP FIND (search-service fallback, latent): SearchService.SearchItemsFuzzyAsync set ArtistIds AND MediaTypes independently when a caller passed both; no current caller passes artistIds (verified: all 11 call sites checked; only PlayRadioIntentHandler passes mediaTypes, without artistIds, which is the legitimate MediaTypes use). The two assignments are now if/else-if so the filters can never ride one query, with the JF-358 comment naming the PlayRadio shape as the reason MediaTypes stays. Behavior-neutral today, pins the invariant against the first future artist-scoped caller.

DOCUMENTED SURVIVORS (MediaTypes with a load-bearing reason, NOT ArtistIds queries, untouched): YesIntentHandler.PlayAlbum/PlayBook chapter queries are ParentId-scoped with the existing JF-361/JF-339 comment (AudioBook chapters are BaseItemKind.AudioBook; IncludeItemTypes=Audio would drop them) and their own pin (HandleAsync_AlbumType_AudioBookItem_UsesMediaTypesForChapters). SearchMediaIntentHandler's remaining MediaType uses are item-property reads, not query terms.

PINS (4 new tests; one pre-existing test reshaped with the fix):
- YesIntentHandlerTests.HandleAsync_ArtistType_SongQuery_UsesIncludeItemTypesNotMediaTypes: disambig_type=artist confirm drives PlayArtist; captured query has ArtistIds containing the artist, IncludeItemTypes containing Audio, MediaTypes null-or-empty, and the play directive still launches.
- QueryArtistLibraryIntentHandlerTests.HandleAsync_TracksByArtist_QueryUsesIncludeItemTypesNotMediaTypes: the tracks listing's captured query (last GetItemList call) carries IncludeItemTypes=Audio, no MediaTypes; the list still speaks the tracks.
- SearchMediaIntentHandlerTests.HandleAsync_ArtistFallback_QueryUsesIncludeItemTypesWithoutMediaTypes: the artist-fallback items query (call 3 of the JF-666 review shape) carries ArtistIds + IncludeItemTypes=Audio, no MediaTypes.
- SearchServiceTests.SearchItemsFuzzyAsync_BuildsBoundedFallbackQueryShape: PRE-EXISTING test, its final assertion MOVED WITH THE FIX (it pinned the removed combination: MediaTypes present alongside artistIds; the "existing tests unmodified" constraint cannot hold for a test asserting the exact behavior removed; the inputs are unchanged, only the expectation follows the new else-if suppression). Companion NEW pin SearchItemsFuzzyAsync_MediaTypesApply_WhenNoArtistIds keeps the PlayRadio shape (MediaTypes applies when artistIds absent) green on both sides of the change.

RED PROOF: production files reverted to HEAD, all five tests run on net9.0: 4 FAILED (the three handler shape pins + the reshaped SearchService assertion), 1 PASSED (the companion, expected green on both sides). Fix restored from the saved patch; full suite re-run green.

SUITES: 4739/4739 PASSED, 0 failed, 0 skipped, exit 0, on BOTH TFMs (single MSBuild node, never --no-build on the green runs; the --no-build isolation runs only after a build with zero source deltas). Baseline on this worktree's main was 4735; +4 new tests = 4739.

VERIFICATION TAIL: AC #5 (live spot check on the 12.1.0 box: confirm-artist yes-path plays tracks, "what tracks does X have" lists them) is NOT done in this worktree round by dispatch scope (worktree agent: commit, never push, never deploy). It rides the orchestrator's post-merge deploy round like JF-666's live bar: after deploy, confirm an artist disambiguation with "si" on an artist with tracks (expect playback, not NoSongsForArtist) and ask "che brani ha <artista>" (expect the list).
<!-- SECTION:NOTES:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 YesIntentHandler.PlayArtist's artist-songs query filters via IncludeItemTypes=BaseItemKind.Audio and carries no MediaTypes term, pinned by a test that captures the InternalItemsQuery (the ProgressiveQueueTests capturing-mock pattern)
- [x] #2 QueryArtistLibraryIntentHandler's tracks listing passes an Audio item-type filter (not a MediaType filter) into ListItemsByArtistAsync, with the captured query shape pinned by test
- [x] #3 SearchMediaIntentHandler's artist-fallback query no longer carries MediaTypes alongside IncludeItemTypes (single filtering term), pinned by test
- [x] #4 All existing suites green both TFMs; the three touched handlers keep their current user-facing responses byte-identical for libraries where the old shape still returned rows
- [x] #5 Live spot check on the 12.1.0 box: confirm-artist yes-path plays tracks, 'what tracks does X have' lists them
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-29 as merge on main (deployed with the full checklist, config intact, active DLL byte-verified 342161e0): the three surviving ArtistIds+MediaTypes query sites moved to the IncludeItemTypes shape (YesIntentHandler.PlayArtist, whose confirm-artist yes-path could speak NoSongsForArtist for an artist that has tracks; QueryArtistLibrary's tracks listing; SearchMedia's artist-fallback redundant filter), SearchService.SearchItemsFuzzyAsync can no longer carry both filters on one query (if/else-if at the one sink), and the combination is now unrepresentable at the initializer level: ArtistQueryShapeGuardTests scans every plugin query initializer comment-aware, red-green proven twice. The guard's birth defects were fixed in-tail and are part of the record: the vacuous-green root cause (EnumerateFiles(AllDirectories) on an un-normalized ..-carrying path yields ZERO files on .NET Linux; GetFullPath is load-bearing, pinned by a comment) and three comment-only false positives (raw pre-test + single-alternation strip). AlbumPlayService.BuildAlbumQuery's post-construction ArtistIds documented as the second non-initializer site with its own JF-358 comment. Gates: /simplify 4-angle round (2 negligible skips noted in the notes), gate-marker code-review on the base, code-review high on the final state in the orchestrator transcript (5 findings, all applied: the one-sink doc overclaim, brace-walk blind spots, operationLabel on the suppression log, the 4x MediaTypes pin hoisted to TestHelpers.AssertNoMediaTypesFilter, two banned hyphens). Suites 4740/4740 both TFMs orchestrator-run on the exact merged tree (tree-hash identity verified). LIVE spot checks on the 12.1.0 box: QueryArtistLibrary tracks lists all 13 Norah Jones tracks with names; SearchMedia artist fallback plays the artist (AudioPlayer.Play + APL, card "Norah Jones - Not Too Late"). The yes-confirm path is unit-pinned (the simulator carries no session attributes to drive it in one shot).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
