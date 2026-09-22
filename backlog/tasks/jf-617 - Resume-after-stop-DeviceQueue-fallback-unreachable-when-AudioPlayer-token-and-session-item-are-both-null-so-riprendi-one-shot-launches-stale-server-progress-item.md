---
id: JF-617
title: >-
  Resume after stop: DeviceQueue fallback unreachable when AudioPlayer token and
  session item are both null, so "riprendi" one-shot launches stale
  server-progress item
status: In Progress
assignee: []
created_date: '2026-09-22 15:29'
updated_date: '2026-09-22 16:36'
labels: []
dependencies: []
references:
  - >-
    backlog/tasks/jf-581 - Podcast resume restarts from 0 (UserData write-loss,
    adjacent but distinct).md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Live incident 2026-09-22 16:43 (device battery test 3): user stopped skill music (PlaybackStopped 16:42:03 saved queue item + 14.7s), then said bare "riprendi". The one-shot AMAZON.ResumeIntent arrived with context.AudioPlayer.Token=null and session.FullNowPlayingItem=null. In ResumeIntentHandler.HandleAsync the DeviceQueue fallback (fallback 3) was nested inside `if (!string.IsNullOrEmpty(item_id))`, so in exactly the both-empty shape it was built for it never ran; control fell to fallback 4 (server-side progress), whose query (FindLastPlayedItemWithProgress, IsPlayed=false ordered by DatePlayed desc) returned a 9-day-stale rewatch-in-progress episode ("Kangaroo Court", 18m4s) — the recently stopped music track was excluded because Jellyfin marks music Played=True on start. The skill launched the stale episode via VideoApp.Launch with a progressive announce the user never asked for. Fix: hoist the queue lookup to run when item_id is empty OR offset is 0, preserving the displaced-token guard. Verified live-server data: queue held the correct item; fallback-4 ordering itself is correct for its remaining cold-queue scope (music exclusion matches Jellyfin resume semantics; LaunchRequestHandler's offer path already consults the queue unconditionally so it does not share the bug).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A one-shot AMAZON.ResumeIntent with null AudioPlayer token AND null session now-playing item resumes the device queue's CurrentItemId at CurrentPositionTicks (regression test ResumeIntent_FallsBackToDeviceQueue_WhenTokenAndSessionItemAreBothNull, fails pre-fix, passes post-fix)
- [ ] #2 The displaced-token guard still prevents a queue item from overriding a different currently-playing token (existing ResumeIntent_DoesNotUseDeviceQueue_WhenTokenMismatch stays green)
- [ ] #3 A cold queue with empty context still falls through to server-side progress and NoMediaPlaying (existing ResumeIntent_ReturnsNoMedia_WhenSessionHasNoNowPlayingItem stays green)
- [ ] #4 Full unit suite green on both TFMs; fix deployed to minix (net10.0) and device-verified: after stopping skill music, bare 'riprendi' resumes the same track near its stop position, not an unrelated episode
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-22 18:35: fix deployed to minix (net10.0 Release, active DLL size-verified, config survived, 1 user). it-IT model rebuilt SUCCEEDED. The wrapper battery's profile-nlu rows confirm the routing surface is intact post-rebuild (mettere/aggiungere/riprodurre/riprendi all PASS, including the new playlist infinitive twin). Review round applied: materialization through the library (deleted-item fallthrough, Played-elsewhere gate, content-kind gate with the AudioBook-before-Audio type pattern), tail threading of the resolved BaseItem (codec probe + audiobook branch see a real item), GetQueue instead of GetOrCreateQueue, dead disjunct dropped, TicksToMs clamp helper. Two new hardening tests (deleted-item, EAC3-empty-context). Truth-source divergence filed as JF-619. REMAINING: device verification of the post-stop one-shot 'riprendi' (Paolo's battery test 3 re-run).
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
