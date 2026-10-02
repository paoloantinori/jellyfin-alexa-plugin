---
id: JF-714
title: >-
  JF-714 - gate the five screenless-Tell video sites' NowPlaying writes on
  HasLaunchDirective (phantom state on screenless devices)
status: Done
assignee: []
created_date: '2026-10-02 15:20'
labels:
  - playback
  - refusal-contract
  - bug
dependencies:
  - JF-699
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-699 merge
(commit 65821618, finding 2 of 5). Five handlers write session.NowPlayingQueue /
FullNowPlayingItem after builder calls that can still return a NON-LAUNCH capability
Tell with a configured secret: BuildVideoAppLaunchResponse returns the
VideoRequiresScreen Tell on screenless devices (PlaybackLaunchBuilder.cs:969-976), so
"play random movie" / "recommend a movie" / "search for <movie>" / the video confirm
arm on a screenless Echo (Dot) write NowPlaying state for a launch that will not
happen; a later "what's playing" answers the phantom movie and next/previous advance a
queue that never started. Pre-existing behavior (the writes ran before the build
before), but the JF-699 diff moved exactly these lines and StartOverIntentHandler
keeps a HasLaunchDirective gate for the identical shape, whose comment labels this
case NOT tautological.

SITES: PlayVideoIntentHandler.cs ~238, SearchMediaIntentHandler.cs ~577,
PlayRandomIntentHandler.cs ~197, RecommendIntentHandler.cs ~221 (the movie arm whose
in-code note documents the skip), YesIntentHandler.cs ~470.

THE WORK: wrap each site's two writes in `if
(PlaybackLaunchBuilder.HasLaunchDirective(response))` (the StartOver pattern; audio
and real-video paths carry directives so their writes are unaffected). Add ONE pin:
the screenless movie arm (any of the five sites with the cheapest existing harness)
returns the capability Tell AND leaves NowPlayingQueue/FullNowPlayingItem unset; the
launch path still writes. Verify no existing test pins the phantom-write behavior
(search for the screenless Tell in the five handlers' test classes before flipping).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (full-solution build green, 0 errors; the only 2 warnings are the pre-existing xUnit1030 in VideoAudioControllerTests.cs:1337, a file this task never touched)
- [x] #2 dotnet test passes (4958/4958 on BOTH net9.0 and net10.0, run twice: after the gate flips and again after the /simplify fixes; the second net9.0 leg re-verified explicitly with a no-build run)
- [x] #3 No new compiler warnings introduced (verified: the 2-warning set is byte-identical to the pre-change build's)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A unchanged: no session-attribute shape touched; QueueItem stays the existing DTO)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A unchanged: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction-model change)
- [x] #7 E2E test added for new intent or handler logic (covered at the unit level instead: the pin extends the existing VideoAppCapabilityGateTests PlayVideo screenless/launch pair, the harness that already drives the real screenless capability Tell end-to-end through HandleAsync; the SMAPI e2e fixtures cannot express a screenless-device context, so the capability-gate unit harness IS the cheapest existing harness the task prescribed)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings; the capability Tell keeps the existing localized VideoRequiresScreen)
- [x] #9 /simplify passed (4 angles run: reuse/simplification/efficiency/altitude. APPLIED 3 findings: PlayRandom's up-to-500-entry queueItems list moved inside the gate as a Select so the screenless-Tell path no longer allocates it dead; Recommend's single-item queue folded into the gate; PlayRandom's duplicated JF-714 rationale deduplicated back to the pre-diff JF-699 text above the branch, one comment at the gate. SKIPPED with reason and FILED the AttachNowPlayingIfLaunched extraction as JF-718 Finding 3: the task prescribes the per-site StartOver wrap and the extraction spans the 3 pre-existing shipped gate sites, the JF-702 to JF-715 precedent. The altitude angle confirmed the handler-side gate IS the settled JF-693/JF-699 altitude)
- [x] #10 /code-review high passed (0 correctness findings in the diff; the reviewer ran its own compile plus targeted suites plus the full net9.0 suite corroborating 4958/4958. 4 out-of-diff findings of the same bug class, every one verified against source and FILED in JF-718: the channel builder writes-before-capability-gate; PlayEpisode/TvNextUp/AplUserEvent writes-before-build; sibling-site pin coverage plus the fresh-session nuance; the extraction. No tracker action left open)
<!-- DOD:END -->

## Final Summary

Implemented 2026-10-02 on the agent worktree (branch off 73325c8d). All five
sites got the StartOver-pattern gate: PlayVideoIntentHandler (~238),
SearchMediaIntentHandler.PlayItem (~577), PlayRandomIntentHandler (~197),
RecommendIntentHandler (~221, whose in-code "pre-existing behavior and
unchanged here" note was REWRITTEN to name the JF-714 closure),
YesIntentHandler.PlayVideo (~470). The phantom-state class at each site is
exactly the two session fields (verified: no other `session.* =` writes and no
QueueContinuationStore/DeviceQueueManager writes exist in the five handlers;
the Yes resume-arm siblings at 246/290 were checked and are throw-or-launch,
not the class). Audio arms and real-video arms keep their writes (directive
present); episodes on screenless devices degrade to the audio-only launch,
which carries a directive, so their writes survive the gate by design.

PIN (the cheapest existing harness, as prescribed): the
VideoAppCapabilityGateTests PlayVideo pair was extended in place rather than
contradicted (pre-check confirmed no existing test pins the phantom write; all
existing NowPlayingQueue/FullNowPlayingItem assertions sit on
directive-carrying paths or fail-open capable contexts). The screenless leg
now asserts the capability Tell AND Null FullNowPlayingItem + Empty
NowPlayingQueue (the MusicPathLaunchRefusalTests assertion shape); the launch
leg asserts the writes still land (Assert.Same on the movie, single queue
entry).

RED PROOF (run, not compensated: the permission layer allowed this sabotage,
unlike JF-702's): with the PlayVideo gate flipped to `if (true)`,
PlayVideo_ScreenlessDevice_VideoRequiresScreenTell FAILS on both TFMs while
the launch leg stays green; gate restored, both green. The pin is
load-bearing.

Gates: /simplify (4 angles, 3 applied / 1 filed to JF-718) and code-review
high (0 in-diff findings, 4 filed to JF-718) as literal Skill calls in the
worker transcript; full suites Bash-run green on both TFMs AFTER the last
source edit (4958/4958 net9.0 + net10.0). Residuals of the sweep filed
same-turn as JF-718 (the live channel-builder instance of the same bug class,
the writes-before-build leftovers, the extraction, the structural pin). No
deploy, no push; orchestrator merges via review.
