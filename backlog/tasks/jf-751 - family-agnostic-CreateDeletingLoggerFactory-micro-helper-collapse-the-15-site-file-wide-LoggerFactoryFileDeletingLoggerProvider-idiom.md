---
id: JF-751
title: >-
  family-agnostic CreateDeletingLoggerFactory micro-helper: collapse the 15-site
  file-wide LoggerFactory+FileDeletingLoggerProvider idiom
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-04 19:52'
labels:
  - test-hygiene
dependencies: []
references:
  - >-
    backlog/tasks/jf-692 -
    extract-a-shared-song-path-vanish-fixture-helper-from-the-four-near-identical-song-vanish-pins.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn from the JF-692 /simplify round (altitude agent, filed-as-out-of-scope per the review-recommendation discipline; JF-692's reserved number is JF-751).

THE SHAPE: in Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs the block

    using var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Trace);
        b.AddProvider(new FileDeletingLoggerProvider("<trigger>", playlistPath));
    });

appears 15 times file-wide across the song, episode, episode-audio and audiobook vanish families (line census at filing time: 7983, 8024, 8061, 8189, 8483, 8555, 8601, 8650, 8763, 8793, 8838, 8888, 8952, 9015, 9068), varying only on the trigger string and optional extra providers (one site adds TestCaptureLogger.Into, one adds a SecretClearingLoggerProvider, one site is provider-less). The drift surface is the trigger strings and the Trace minimum-level convention.

WHY JF-692 DID NOT TAKE IT: JF-692's mandate was the four song pins' arrange (item+mocks+paths, the SetupAudiobookVanishFixture sibling cut); the factory block is a CROSS-FAMILY idiom and does not belong in the SetupXxxVanishFixture lineage, and absorbing it would have widened a test-hygiene arrange-only diff by 15 call sites.

FIX DIRECTION: a family-agnostic micro-helper, e.g. `CreateDeletingLoggerFactory(string trigger, string playlistPath, params ILoggerProvider[] extra)` returning the built ILoggerFactory, preserving the Trace minimum and the provider order (extras after the deleting provider, matching today's AddProvider sequence at the capture/clearing sites). Migrate the 15 sites; the two-pin named-provider-var shapes (the JF-685 Fired assert and the JF-682 twin's named providers) keep their named locals via the returned factory being a using var and the providers being constructed by the caller and passed in as extras, so Fired asserts stay intact.

SECOND IDIOM, same class (added 2026-10-04 from the JF-692 /code-review high round, finding 3): the `_mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg")` wire-up is triplicated across the three sibling fixtures SetupSongVanishFixture / SetupAudiobookVanishFixture / SetupEpisodeForHls (VideoAudioControllerTests.cs, same file); a future encoder-mock convention change must be mirrored in all three. A shared wire-up may fold into this task's helper or its own; a helper consumed by only ONE sibling fixture is vacuous, so the EncoderPath consolidation needs all three fixtures converted together (that conversion exceeds the four-song-pin surface JF-692 was mandated, hence tracked here rather than done there).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code touched)
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [ ] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [ ] #7 E2E test added for new intent or handler logic (N/A: test-hygiene refactor only)
- [ ] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Done 2026-10-04. The 15-site LoggerFactory+FileDeletingLoggerProvider idiom in VideoAudioControllerTests.cs collapsed into CreateDeletingLoggerFactory(string trigger, string playlistPath, params ILoggerProvider[] extraProviders) plus a provider-first overload for the two Fired-asserting named-local sites; arrange-only, zero Assert/Fact lines changed (grep-proven), provider registration order and Trace minimum preserved byte-faithfully at every site (extras first, deleting last, today's actual sequence; the filing's "extras after" parenthetical described the order backwards). Non-vacuity: sabotage of the helper body (deleting provider dropped) redded 14 of 15 consumers on BOTH TFMs; the 15th (the JF-682 twin) stays green by design per its own doc (its Fired assert is the negative one). The SECOND IDIOM (EncoderPath mock triplication) was evaluated live and NOT shipped: the wire-up is inert at every fixture consumer (FfmpegPath override wins in ResolveFfmpegPath; a broken shared setup left all 266 class tests green on both TFMs), which fails the JF-692 non-vacuity bar - filed as JF-759 with the sabotage evidence instead. The simplify altitude round's follow-up (capture-only Trace factory idiom, ~21 sites + a SkillResponseLoggingTests sibling) filed as JF-760. Gates: Skill simplify (4 angles: doc-wording finding applied; reuse+efficiency CLEAN) + Skill code-review high (zero correctness findings; one low readability finding on argument-order-vs-registration-order applied as the provider overload's MIND THE ORDER doc contract). DoD evidence: #1+#3 solution Release --no-restore -warnaserror build 0 warnings 0 errors; #2 suites 5162/5162 BOTH net9.0 and net10.0 on the final state (baseline 5162, count unchanged); #4-#8 N/A per the boxes' own text; #9+#10 the gate transcripts above. One transient observation, not a regression: a single first-post-build filtered run had 1 net10.0 failure in the JF-731 Dispose backstop (the documented cross-test-sweep timing class); three subsequent filtered runs and the full suite were fully green on both TFMs.
<!-- SECTION:FINAL_SUMMARY:END -->
