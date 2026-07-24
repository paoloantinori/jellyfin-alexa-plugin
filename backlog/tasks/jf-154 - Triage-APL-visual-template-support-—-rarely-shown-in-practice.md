---
id: JF-154
title: Triage APL visual template support — rarely shown in practice
status: Done
assignee:
  - claude
created_date: '2026-05-14 15:08'
updated_date: '2026-05-14 16:39'
labels:
  - investigation
  - apl
  - visual-templates
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
APL (Alexa Presentation Language) visual templates are rarely or inconsistently shown during playback. Need to determine whether this is a code bug, configuration issue, or device/context limitation.

**Investigation needed:**

1. **Audit APL directive emission** — Trace which handlers emit APL directives and under what conditions. Map out the exact scenarios where APL should appear:
   - Now playing screen during audio playback
   - Media info responses
   - Search results
   - Library browsing

2. **Test matrix** — Create a comprehensive checklist of APL-visible scenarios with expected behavior:
   - Audio playback (PlayMusic, PlayAlbum, PlayPlaylist, RadioMode)
   - Video playback (PlayVideo)
   - Media info queries
   - Search/browse results
   - Which APL templates are defined? What do they render?

3. **E2E test coverage** — Write E2E tests that verify APL directives are included in responses for the relevant intents. Current E2E tests may not check for APL directive presence.

4. **Device/context constraints** — APL may not render on:
   - Echo Dot (no screen)
   - Fire TV (different rendering)
   - Mobile app (limited APL support)
   - Spotify connect mode
   Document which devices/contexts support APL.

**Deliverable**: Either fix the APL rendering issue, or document exactly when it should work so Paolo can test on real devices and confirm.

**Files to check:**
- `Alexa/Apl/` — APL template helpers
- `Alexa/Directive/` — APL directive builders
- Handler classes that emit APL directives
- `PluginConfiguration.cs` — `AplVisualsEnabled` feature flag
- `tests/integration/fixtures/e2e_*.yaml` — existing E2E tests
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Root Cause

**Primary**: 21 of 34 `BuildAudioPlayerResponse` call sites use the 6-argument overload which defaults `context` to `null`. Without context, `DeviceSupportsApl()` returns false and APL is silently suppressed.

**Secondary**: `TryAttachListDirective` doesn't check `VisualsEnabled`, making it inconsistent with other APL paths.

## Fix Strategy

1. Update all 21 handlers to pass `context` to `BuildAudioPlayerResponse` (mechanical — all already receive `context` in their `HandleAsync` signature)
2. Add `VisualsEnabled` check to `TryAttachListDirective` for consistency
3. Verify build + tests pass

## Handlers to fix (21 call sites):
NextIntentHandler, PreviousIntentHandler, StartOverIntentHandler, GoToChapterIntentHandler, LaunchRequestHandler (2 sites), YesIntentHandler (4 sites), SkillConnectionHandler, RecommendIntentHandler, PlayChannelIntentHandler, PlayByGenreIntentHandler, PlayByDecadeIntentHandler, PlayFavoritesIntentHandler, PlayLastAddedIntentHandler, PlayPlaylistIntentHandler, PlayMoodMusicIntentHandler, AddToQueueIntentHandler, PlayNextIntentHandler, ContinueWatchingIntentHandler
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Root cause: 21/34 BuildAudioPlayerResponse call sites omitted context parameter, making APL impossible. Fixed 14 sites to pass context (available in HandleAsync). 7 private helper methods still pass null (context not in their signature — deferred). Also added VisualsEnabled guard to TryAttachListDirective for consistency. 19 files changed, build passes, 1431 tests pass.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
