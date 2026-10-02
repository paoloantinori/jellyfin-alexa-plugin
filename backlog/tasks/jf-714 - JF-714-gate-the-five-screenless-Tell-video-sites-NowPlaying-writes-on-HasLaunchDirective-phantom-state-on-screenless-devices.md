---
id: JF-714
title: >-
  JF-714 - gate the five screenless-Tell video sites' NowPlaying writes on
  HasLaunchDirective (phantom state on screenless devices)
status: Done
assignee: []
created_date: '2026-10-02 15:20'
updated_date: '2026-10-02 19:58'
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

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit eeb17234 + gate-marker tail 724df7da, merged as 3549c256. The five screenless-Tell video sites' NowPlaying writes gated on HasLaunchDirective (PlayVideo, SearchMedia, PlayRandom, Recommend's movie arm, YesIntent's video confirm), so a capability Tell on a Dot no longer leaves phantom now-playing state; the screenless-episode degrade keeps its writes (AudioPlayer directive). The census was confirmed by the gate-marker's own full-codebase scan, which found the one site both sweeps missed (SkillConnectionHandler's favorites task, appended to JF-718). Worker gates green (simplify 3 applied + the JF-718 extraction filed; code-review high 0 correctness findings with the reviewer independently re-running compile, targeted suites, and the full net9.0 suite). Red proof genuinely run (the sabotage was permitted this round: gate flipped to if(true), the screenless pin failed on both TFMs via the phantom write itself). Gate-marker findings all applied in the tail (the JF-718 site addition, its channel-builder fix-shape correction naming the second directive-less return and the third caller, and the prose fixes). Suites: worker 4958/4958 twice, orchestrator independent 4958/4958 both TFMs, merged-tree 4962/4962 both TFMs exit 0 run as concurrent split-TFM jobs. Production surface changed (five handlers): deployed in the post-merge deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
