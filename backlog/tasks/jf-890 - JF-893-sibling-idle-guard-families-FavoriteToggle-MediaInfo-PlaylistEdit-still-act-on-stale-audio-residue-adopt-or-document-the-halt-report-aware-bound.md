---
id: JF-890
title: >-
  JF-893: sibling idle-guard families (FavoriteToggle, MediaInfo, PlaylistEdit)
  still act on stale-audio residue; adopt or document the halt-report-aware
  bound
status: To Do
assignee: []
created_date: '2026-10-15 17:30'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-892 simplify/altitude review (2026-10-15). The JF-892 doctrine (an explicit halt report with no corroborating live evidence withdraws the frozen AudioPlayer token as liveness proof; see PlaybackLaunchBuilder.IsStaleAudioPlaybackResidue, consumed by the ONE loop/shuffle write-family gate ProgressReporter.TryAnswerModeWriteMediumGate) is scoped to the write family only. The OTHER HasCurrentPlaybackEvidence-guarded families still act on the same stale-audio-residue shape: on an idle device after music (frozen song token, playerActivity=STOPPED, empty session, Audio-routed ledger) the guard's TOKEN leg passes (PlaybackLaunchBuilder.HasCurrentPlaybackEvidence treats any token as evidence) and ResolveCurrentPlayingItem's token leg resolves the dead item, so FavoriteToggle favorites it, MediaInfo reports it as now-playing, and PlaylistEdit mutates the queue around it. Verified from source: FavoriteToggleIntentHandler.cs:96 (guard) then :104 (resolver call); the same structure in MediaInfoIntentHandler.cs:97 and PlaylistEditHandlerBase.cs:174. Unlike JF-890's video residual this one is not recorded as an accepted residual anywhere. Decide per family: adopt the halt-report-aware evidence leg (with its own red proofs per family; the JF-789 recency fold was evaluated and rejected on this predicate's doc, but the halt-report fold never was) or document it as a deliberate accepted residual. Do NOT fold it into the write-family gate; these families do not call that gate.
<!-- SECTION:DESCRIPTION:END -->
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
