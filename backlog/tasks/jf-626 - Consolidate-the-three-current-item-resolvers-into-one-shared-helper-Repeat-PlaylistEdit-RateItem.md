---
id: JF-626
title: >-
  Consolidate the three current-item resolvers into one shared helper (Repeat /
  PlaylistEdit / RateItem)
status: Done
assignee: []
created_date: '2026-09-25 00:05'
updated_date: '2026-09-25 02:21'
labels:
  - refactor
  - tech-debt
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/RepeatIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaylistEditHandlerBase.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/RateItemIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The JF-326 review (2026-09-25, three-pass simplify) confirmed the codebase now holds THREE hand-rolled variants of "resolve the currently playing item" that have already drifted semantically:

1. RepeatIntentHandler (Alexa/Handler/Intent/RepeatIntentHandler.cs, inline in HandleAsync): raw Guid.TryParse on the AudioPlayer token, session.FullNowPlayingItem fallback, device-ledger fallback with the videoDisplacedAudio arbitration.
2. PlaylistEditHandlerBase.ResolveCurrentItem (Alexa/Handler/Intent/PlaylistEditHandlerBase.cs): StreamTokenCodec token parse, session.NowPlayingItem (DTO re-resolved by id), no ledger arm.
3. RateItemIntentHandler.ResolveCurrentItem (Alexa/Handler/Intent/RateItemIntentHandler.cs, added by JF-326): StreamTokenCodec token parse, session.FullNowPlayingItem fallback, device-ledger fallback with the same arbitration, cheap-guards-before-resolve ordering.

Known divergences that matter (from the review):
- Repeat parses the token RAW, so a sleep-timer composite token ("{guid}|sleep:{ticks}", JF-447) silently declines to the session/ledger legs while a timer is armed; the codec-safe shape is already in the other two. Verify whether this changes Repeat's resolved item in the armed-timer shape and fix as part of the consolidation.
- PlaylistEdit re-resolves session.NowPlayingItem.Id through the library when FullNowPlayingItem carries the same item free.

Goal: ONE item-returning resolver (beside PlaybackLaunchBuilder.ResolvePlayingMedium in Alexa/Util/PlaybackLaunchBuilder.cs, which already owns the classification policy and documents the Repeat exception) consumed by all three handlers, preserving each caller's arbitration semantics; the JF-447 codec comment and the videoDisplacedAudio predicate get one home. Do not hand-write the arbitration a fourth time (the IsVideoAppLaunchItem "do not hand-write the type list again" rule is the precedent).
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
JF-626 complete: ResolveCurrentPlayingItem is the ONE current-item resolver (beside ResolvePlayingMedium in PlaybackLaunchBuilder), the kind ladder extracted to ClassifyLedgerItemKind so the classifier and the displacement predicate cannot drift again. Repeat drops its inline arbitration and gains the seek-mode medium gate (CannotRepeat instead of double audio); RateItem deletes its private copy; PlaylistEdit delegates with the ledger arm off. REAL BUG fixed: the sleep re-launch never records the ledger, so Repeat's raw token parse restarted the OLDER track mid-album with a composite token armed (pinned by test); GetLastPlayedSnapshot closes the torn ledger read. Worker ran both gates in-turn (/simplify 4 agents; code-review high, 9 findings: 6 applied, JF-627/628/629 filed). Merged state re-verified 4315x2 and deployed.
<!-- SECTION:FINAL_SUMMARY:END -->
