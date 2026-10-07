---
id: JF-803
title: >-
  JF-803 - the shared deep-resume re-page lift across the album and book heads
  (the natural JF-797 item 3 companion)
status: To Do
assignee: []
created_date: '2026-10-07'
labels:
  - refactor
  - playback
dependencies:
  - JF-796
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/AudiobookPlayResolver.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/QueueContinuationFetcher.cs
priority: low
---

## Description
<!-- SECTION:DESCRIPTION:BEGIN -->
Filed by the JF-796 worker (2026-10-07), same-turn per the review-recommendation
rule, from the /simplify altitude round on the album deep-resume block. JF-796
landed the album head's bounded deep resume (page-1 miss + InitialPageHasMore =>
one unpaged scoped fetch + re-run FindResumeTrackIndex + Skip/Take re-slice +
continuation rebase) in AlbumPlayService's own idiom, mirroring the JF-793 book
block in AudiobookPlayResolver.PlayBookAsync shape for shape. The JF-796 task
contract allowed exactly that (the fold into PlayBookAsync was declined because
the sites diverge materially: the JF-625 tracker veto axis, the
resumePosition/device-queue axis, the AlbumIds arm axis, and the absolute concat
prefix). The altitude reviewer's residual is real though: the ~25-line re-page
ALGORITHM now exists twice, and the diff's own comments declare the coupling
("the JF-797 item 3 discriminator applies here too once it lands"), so any
change to the guard, the re-slice size, or the rebase must land in lockstep or
album and book resume drift.

The lift (NOT into PlayBookAsync; one layer down, beside the primitives both
heads already share: InitialPageHasMore, the scoped unpaged builders,
FindResumeTrackIndex): a TryDeepResumeRePageAsync helper taking the fetch and
the rescan as delegates (the fetch closure carries the album's AlbumIds arm and
executor, the rescan closure carries each site's FindResumeTrackIndex overload),
returning the re-sliced page, the deep index, the full list, and the rebased
continuation triple; each caller keeps its own guard terms (the album's
!trackerOverrideEngaged veto stays a caller-side condition) and applies the
results (the album's absolute concat prefix reads the returned full list; the
book head ignores it). The reviewer's claim to verify when implementing: the two
sites' guards are the same predicate because FindResumeTrackIndex returns ticks
0 whenever resumePosition is false.

Why a task and not JF-796 itself: the lift rewrites the JF-793/JF-795 book block
(a surface outside the JF-796 diff, freshly merged by the previous worker), and
the four divergence axes survive as delegates at each call site, so the net is
about 25 shared lines versus two guarded closure-carrying call sites. The
natural trigger is JF-797 item 3 (the pre-fetch Played/position discriminator
that must touch BOTH sites anyway): implement the discriminator once inside the
lifted helper rather than twice inline. Rule of three: two copies plus that
filed third change.

Test-side note from the same review round (the JF-465 fixture-adoption debt
class): the paging-honoring GetItemsResult mock lambda now has five hand-rolled
copies across the suites (AlbumDeepResumeTests.SetupDeepResumeAlbum and the
AlbumAnnounceVehicleTests veto pin are the two newest); when touched again, hoist
a paging-honoring SetupAlbumPages helper into TestHelpers beside
SetupAlbumTracks. JF-805 /simplify census update (2026-10-07): the sixth copy
landed as YesIntentHandlerTests.SetupConfirmedAlbum (the album twin of
SetupConfirmedBook; same Skip/Take paging lambda, the 4-minute-runtime track
factory, and the progress UserData block, parameterized differently from
SetupDeepResumeAlbum). The hoist stays skipped there for the JF-796 round's
reason (private fixtures over different mock backing: PluginTestBase mocks vs
HandlerTestFixture); this census is the same-turn landing the skip rule demands.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
