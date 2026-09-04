---
id: JF-481
title: >-
  Cleanup: shared IsActivelyPlaying helper, radio FINISHED/null-context elicit
  shapes, test scaffolding dedupe, InteriorContainment naming residue
status: Done
assignee: []
created_date: '2026-09-04 10:53'
updated_date: '2026-09-04 13:31'
labels: []
dependencies: []
references:
  - JF-478/479/480 review pass (2026-09-04)
  - PlayRadioIntentHandler.cs
  - PlaybackFinishedEventHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Landing spot for the below-threshold items from the JF-478/479/480 review pass (2026-09-04), per the every-recommendation-lands rule:

1. Radio shared helper: the actively-playing idiom (playerActivity == PLAYING || BUFFER_UNDERRUN) now exists verbatim in PlayRadioIntentHandler.cs ~:89 and PlaybackFinishedEventHandler.cs ~:77. Extract BaseHandler.IsActivelyPlaying(context); keep ResumeIntentHandler's intentionally narrower PLAYING-only check separate (document why in the helper doc). Two-site lockstep change risk otherwise.
2. Radio elicit residual shapes (both self-recover, scored ~55-60, unverified on device): (a) playerActivity FINISHED (natural queue exhaustion) with a multi-room surviving session item now elicits instead of seeding radio from the item; (b) a request whose context carries no AudioPlayer object at all while a group member actively plays. Decide whether FINISHED should seed radio (probably yes: the queue just ended, radio continuation is the PostPlay-adjacent behavior) and verify on device.
3. Test scaffolding dedupe: the two StartsRadioMode radio tests duplicate the library/user mock pair; the three new CrossMediaTypeFallbackTests repeat the album/artist/tracks triage callback; the two PlayAlbum mirrors repeat the mock pair. Local helpers would dedupe. Also cosmetic: the two JF479 CrossMediaTypeFallbackTests comment blocks have continuation lines at 4-space indent under an 8-space body.
4. Naming residue: the historical test method names carrying InteriorContainment_ (PlaySong_InteriorContainmentAlbum_NotSubstituted and the JF-478-era pins) predate the IsEmbeddedContainment rename; rename for grep-coherence or leave with a note on the renamed method that the old class name was 'interior containment' (the production API is already renamed).

Consolidation/cleanup only: no behavior change except item 2a if the FINISHED decision lands.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All four items landed (commit 7c1bf3e8), deployed.

1. BaseHandler.IsActivelyPlaying(Context) extracted (PLAYING || BUFFER_UNDERRUN, null-tolerant); the two verbatim sites adopted (PlayRadioIntentHandler, PlaybackFinishedEventHandler); ResumeIntentHandler deliberately unconverted with the rationale in the helper doc; FINISHED stays out of the helper by design (the PlaybackFinished queue logic treats it as exhausted; the reviewer confirmed the altitude split correct).
2. The FINISHED decision IMPLEMENTED as provisional: queueJustFinished (playerActivity FINISHED) with a surviving session item now seeds radio mode (PostPlay-adjacent continuation) instead of eliciting; two named locals gate the branch; the seeding path untouched; the reviewer walked the surviving-LiveTvChannel RadioNotAudio hazard and judged it unreachable without stale cross-player state (AudioPlayer FINISHED only comes from Audio items; VideoApp playback does not drive playerActivity) and pre-existing for the analogous PLAYING shape. The device observation that would tighten it stays in the task's own item-2 list.
3. Test dedupe as specified (SetupSimilarTrackQuery, SetupAlbumMissArtistWithSongs with SubStrict delegating; the one remaining inline mock uses a named id the test asserts on and cannot use the helper, verified); misindented comments fixed.
4. InteriorContainment_ renames to EmbeddedContainment_ across three files, zero grep residue, production identifiers untouched.

Suite 3148/3148, Release 0 warnings, validators baseline. The review pass's independent mutation run corroborated both gates load-bearing and byte-restored the tree before finishing.

Deployed with the JF-473 stream (one DLL); the PauseKeepsSession flag verified still ON across the restart (config persisted).
<!-- SECTION:FINAL_SUMMARY:END -->
