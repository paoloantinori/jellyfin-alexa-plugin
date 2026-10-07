---
id: JF-805
title: >-
  JF-805 - the YesIntent ALBUM confirm leg launches albumItems[0] with no resume scan
  and no continuation: the album twin of the JF-795 confirm==ask unification
status: Done
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-796
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/YesIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/AlbumPlayService.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-796 gate-marker (2026-10-07), finding 3, same-turn per the
review-recommendation rule.

The YesIntent album confirm leg still always launches `albumItems[0]` with no
FindResumeTrackIndex scan and no QueueContinuation mint. The asymmetry class is
pre-existing (in-page progress already diverged), but two things widened it: JF-795
made confirm == ask STRUCTURAL for books via the shared AudiobookPlayResolver while
the album confirm stayed hand-rolled, and JF-796's deep resume makes the divergence
observable in a shape it previously was not (UserData in-progress on track 22 of 26:
the direct ask now launches track 22; a disambiguation yes launches track 1 with the
whole-album unpaged queue and no continuation).

FIX SHAPE (the JF-795 pattern evaluated for albums): route the album confirm through
BuildAlbumPlayResponseAsync the way the book confirm routes through
PlayBookAsync (the PodcastEpisodeResolver/AudiobookPlayResolver precedent chain),
so the confirm inherits the initial page, the deep resume, the continuation mint, and
the tracker override from the ONE composition. Evaluate whether the album composition
extracts as cleanly as the book head did; the four documented divergence axes of
JF-803 (the tracker veto, resumePosition tiers, the AlbumIds arm, the absolute
prefix) live INSIDE the album composition and ride along, they do not block the
routing.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release -warnaserror: 0 warnings, 0 errors, both TFMs)
- [x] #2 dotnet test passes (5439/5439 both TFMs; baseline 5430 + the 9 new pins)
- [x] #3 No new compiler warnings introduced (Release build 0/0 at the final state)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute changes; the routing passes none)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model or utterance changes)
- [x] #7 E2E test added for new intent or handler logic (N/A per the worker split: no new intent; the routing change is pinned by 9 unit tests and the full-suite gate runs on the merged tree before deploy)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings; the confirm reuses the existing responses the composition already speaks)
- [x] #9 /simplify passed (4 angles; S1/S3/R1 applied, S2/R2 skipped with recorded reasons, the R2 census landed same-turn in JF-803's note)
- [x] #10 /code-review high passed (4 findings, all four applied as pins or fixes; the production surface needed no change)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed with the JF-795 pattern applied to albums: the YesIntent MusicAlbum confirm
now routes through AlbumPlayService.BuildAlbumPlayResponseAsync (the
PodcastEpisodeResolver/AudiobookPlayResolver precedent chain), placed as the
third early-return routing branch beside its book and podcast twins, so the
confirm inherits the initial page, the resume scan (in-page and the JF-796 deep
resume), the JF-625 tracker override, the continuation mint, and the
crash-recovery device-queue write from the ONE composition and cannot drift
from the direct ask. ROUTING DECISION: the composition re-fetches the track
page (the books' JF-795 answer, the head's own fetch flow; the disambiguation
state carries only id/name pairs, so nothing prefetched is wasted; the album
geometry does not block it). The music-disabled gate (the JF-611/JF-795 shape)
makes the disabled answer byte-identical to the ask's. The private PlayAlbum
method survives verbatim as the documented defensive-only sibling (no live
producer: book payloads route to PlayBookAsync, MusicAlbums to the composition;
the JF-803 divergence axes ride along inside the composition). The JF-796
one-fetch trade and the already-filed JF-804 tracker-walk bound are unchanged
by construction (same composition).

RED PROOFS on the unmodified tree, both TFMs, failure messages recorded:
DeepProgress_ResumesAtPositionHoldingTrack (Expected the track-22 token, Actual
track 1: the defect verbatim), MintsQueueContinuation (Expected queue 5, Actual
40: the unpaged whole-album queue; the continuation null beyond it),
DeepProgress_ContinuationAtResumeAwareOffset (Assert.NotNull failure:
continuation null), MusicDisabled (the disabled Tell never answered; directives
present), SeekMode_WarmTrackerRidesTheComposition (start=3000000000 not found:
no tracker override). The two no-collateral companions (single page,
default-config silence) were green pre-fix as expected.

CONFIRM == ASK ENUMERATION (every MusicAlbum confirm shape): fresh single-page
(pinned: queue of 3, track 1, no continuation), fresh multi-page (pinned: page
queue 5, continuation 5/40, device queue written), in-page progress (rides the
ONE composition; pinned at composition level in AlbumDeepResumeTests), deep
progress beyond page (pinned: track 22 launches at offset 0, continuation
rebased 26/40), seek mode warm tracker (pinned: the tracker position wins over
deep UserData, VideoApp concat sliced at the tracker's position, single
directive), cold-tracker seek with deep progress (rides the composition;
pinned at composition level, the JF-796 F3 pin), music disabled (pinned:
MediaTypeNotAvailable Tell, no directives, no queue), speech/announce (pinned:
silent under the default config; the locale now flows so AnnounceAudioPlays
speaks for confirm and ask identically), split album (pinned: paged folder arm
plus AlbumIds retry under the JF-666 scope), empty album (pinned: the
NoSongsInAlbum Tell), crash-recovery device queue (pinned: the paged ids land
on the confirming device).

Gates: /simplify 4 angles (S1 the ConfirmAlbumAsync shared driver collapsing
the per-test scaffold and closing the inconsistent-finally drift, S3 the
single config write, R1 WarmTrackerFiveMinutesIn hoisted to TestHelpers
adopted by both suites; S2 the defensive PlayAlbum retained per the JF-361/
JF-672 convention with the comment sharpened to name the guarded scenario,
R2 the paging-mock dedup stays the JF-465/JF-803 debt class with the sixth
copy recorded in JF-803's census this turn; efficiency and altitude clean).
/code-review high: the production intercept verified clean on five axes (gate
byte-parity, payload disjointness, argument match, the JF-674 stale-continuation
discard, the DI queue-manager registration); four test-level findings ALL
applied (F1 the device-queue pin, F2 captured-restore in both disabled pins,
F3 the whole-list single-directive invariant, F4 the empty-album pin). Two
legacy pins adapted to the routed executor (the JF-767 split-album pin now
asserts the paged arms through GetItemsResult; the basic album confirm mock
moved to the composition's page executor with the token asserted).

Suites: 5439/5439 both TFMs (baseline 5430 + 9 pins), Release -warnaserror
0/0, touched and adjacent battery green both TFMs (YesIntent 40/40,
AlbumAnnounceVehicle 4/4, plus the 218+211 adjacent classes). Production
surface changed: deploys (the YesIntent album confirm behavior; no model,
locale, or config changes).
<!-- SECTION:FINAL_SUMMARY:END -->
