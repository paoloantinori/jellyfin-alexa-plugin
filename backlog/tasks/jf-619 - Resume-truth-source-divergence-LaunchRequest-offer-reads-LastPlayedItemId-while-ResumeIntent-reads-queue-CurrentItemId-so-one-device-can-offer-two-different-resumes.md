---
id: JF-619
title: >-
  Resume truth-source divergence: LaunchRequest offer reads LastPlayedItemId
  while ResumeIntent reads queue CurrentItemId, so one device can offer two
  different resumes
status: In Progress
assignee: []
created_date: '2026-09-22 16:21'
updated_date: '2026-09-22 20:09'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-617 -
    Resume-after-stop-DeviceQueue-fallback-unreachable-when-AudioPlayer-token-and-session-item-are-both-null-so-riprendi-one-shot-launches-stale-server-progress-item.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-617 review round (2026-09-22, finding CONFIRMED): bare AMAZON.ResumeIntent (the hoisted DeviceQueue fallback) now prefers queue.CurrentItemId (written by AudioPlayer stop events), while the LaunchRequest resume offer and its YesIntent confirm seed exclusively from DeviceQueueManager.GetLastPlayedItemId + UserData (written by VideoApp launches via LastPlayedResponseInterceptor). Neither path reads the other's store. Concrete divergence: user stops music X on the Echo (CurrentItemId=X), later watches a VideoApp item M on the same device (LastPlayedItemId=M): opening the skill offers 'resume M' while saying 'riprendi' resumes X. Any freshness/ordering fix must be applied to both chains or they drift further; the fix should introduce one shared resolution helper (device-scoped, freshness-aware) rather than divergent inline reads. Related review context: the queue pointer has no aging/clearing on cross-client completion (only ClearQueue/FollowMe reset it), which is why the JF-617 hardening added the Played-elsewhere gate - the same discipline belongs in whatever shared resolver replaces both reads.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A documented precedence order exists for the resume truth sources (device queue pointer vs LastPlayedItemId vs server progress) and every resume entry point applies the same order
- [ ] #2 The scenario 'AudioPlayer stop writes CurrentItemId=X, later VideoApp play writes LastPlayedItemId=M' yields the SAME resume target from LaunchRequest's offer and from bare AMAZON.ResumeIntent
- [ ] #3 Unit tests pin the cross-entry-point consistency on the above scenario
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-22 23:05: SHIPPED (commit 8bb5e511, deployed to minix net10.0). One resolver, GetDeviceResumePointer: fresher write stamp wins; a 30s delayed-stop grace window tilts same-window ties to the launch-time record (a PlaybackStopped landing just after a newer launch is the pause the voice request caused); null stamps (pre-JF-619 files) keep the queue-pointer tie, with the upgrade-window offer flip documented and self-healing. Writers: DeviceQueue.SetCurrentItemPointer is the one writer for the pointer (stop event, nearly-finished, RecordNowPlaying); RecordLastPlayed refreshes its stamp even on the unchanged-item short-circuit. Consumers: LaunchRequestHandler's two offer sites (stale-token equality now checks BOTH stores: NearlyFinished pre-advances the pointer near a song's end) and ResumeIntentHandler fallback 3 (resolver winner THEN loser, per-source kind gating: Movie/Episode from the launch-time arm go to fallback 4's VideoApp resume; ResolveResumeTicks' plugin-store fallback arm always reachable). Review below-cap residuals recorded: per-user custom invocation name not among trap candidates (RuntimeInvocationNameCandidates doc); unlocked id/stamp writes vs unlocked resolver read (the class's own JF-522 two-thread rationale applies); old-DLL rollback strips the stamp members on re-persist (benign: null stamps keep legacy ties). Gates: simplify (equivalent + literal) + code-review high (10 findings: 9 applied, 1 documented) + tests 4231/4231 x both TFMs. Device verification pending (user).
<!-- SECTION:NOTES:END -->

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
