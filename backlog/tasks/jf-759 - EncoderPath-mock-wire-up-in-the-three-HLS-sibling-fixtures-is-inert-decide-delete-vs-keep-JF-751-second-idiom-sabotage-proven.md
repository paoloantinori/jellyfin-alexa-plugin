---
id: JF-759
title: >-
  EncoderPath mock wire-up in the three HLS sibling fixtures is inert: decide
  delete-vs-keep (JF-751 second idiom, sabotage-proven)
status: To Do
assignee: []
created_date: '2026-10-04 19:29'
labels:
  - test-hygiene
dependencies: []
references:
  - >-
    backlog/tasks/jf-751 -
    family-agnostic-deleting-logger-factory-micro-helper-collapse-the-15-site-file-wide-idiom.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn from JF-751's execution (the filing's SECOND IDIOM; JF-751's reserved out-of-scope number).

THE FINDING: the `_mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg")` wire-up, triplicated across the three sibling fixtures SetupEpisodeForHls / SetupSongVanishFixture / SetupAudiobookVanishFixture (Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs), is INERT at every consumer today, so the proposed shared helper fails the JF-692-family non-vacuity bar (the helper's wiring broken once must red the consumers).

EVIDENCE (all verified live 2026-10-04 in the JF-751 worktree):
1. VideoAudioController.ResolveFfmpegPath (Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs, the FfmpegPath-override-first arm) returns the controller's explicit FfmpegPath before ever consulting _mediaEncoder.EncoderPath; EncoderPath is a fallback-only source with a further PATH scan behind it.
2. All 32 fixture call sites in the file pass an explicit fake ffmpeg (a CreateController ffmpegPath argument, positional fake, or a controller.FfmpegPath assignment next to the call); none resolves through the mock.
3. The decisive sabotage: the three fixtures were converted to a shared SetupEncoderPathMock helper and the helper broken (nonexistent path returned); ALL 266 VideoAudioControllerTests stayed GREEN on BOTH TFMs. A load-bearing wire-up would have red its consumers (compare the same round's logger-helper sabotage: 14 of 15 consumers red).

WHY NOT DONE IN JF-751: the JF-692 mandate for this family requires arrange-only consolidation with non-vacuity proven by shared sabotage; a helper whose wiring provably cannot red any consumer is exactly the vacuous-helper shape the JF-751 filing itself warns against, so the EncoderPath conversion was reverted there and this decision filed instead.

FIX DIRECTION (a decision, not a mechanical consolidation): either DELETE the wire-up from the three fixtures (a small semantic change beyond arrange-only: it only matters for a future consumer that constructs a controller without the FfmpegPath override on a machine with no ambient and no PATH ffmpeg, where deletion changes the failure shape from wrong-fake-path to empty-path), or KEEP it as a fallback-only convention and document that at one site. NOTE for whoever takes this: the file carries ~60 further INLINE EncoderPath setups outside the three fixtures with the same inertness question (all encode-running tests also pass an explicit fake); sample a few before deciding whether the cleanup is fixture-scoped or file-wide.
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
