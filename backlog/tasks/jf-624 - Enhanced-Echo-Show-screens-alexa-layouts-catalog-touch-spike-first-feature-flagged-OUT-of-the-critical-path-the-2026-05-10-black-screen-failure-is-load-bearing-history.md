---
id: JF-624
title: >-
  Enhanced Echo Show screens (alexa-layouts catalog + touch) - spike-first,
  feature-flagged, OUT of the critical path (the 2026-05-10 black-screen failure
  is load-bearing history)
status: In Progress
assignee: []
created_date: '2026-09-23 19:50'
updated_date: '2026-09-24 16:12'
labels: []
dependencies: []
references:
  - claudedocs/research_echo-show-apl-screens-touch_2026-09-23.md
  - >-
    commit 29d49e16 (the 2026-05-10 alexa-layouts removal: black screen, silent
    failure)
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-23 exhaustive research (claudedocs/research_echo-show-apl-screens-touch_2026-09-23.md). HISTORY THAT MUST SHAPE THIS (the past failure Paolo recalls, reconstructed from git): on 2026-05-10 (commit 29d49e16) the alexa-layouts 1.5.0 import caused SILENT rendering failure (APL black screen) alongside a dynamic-entities budget overflow; the fix REMOVED the import entirely and replaced @viewportProfile with native viewport checks, and every template since is hand-rolled. Today's catalog is 1.7.0 and our documents declare APL 1.7, so the compatibility question must be re-verified empirically, never assumed.

Plan (spike-first, out of the critical path):
1. SPIKE: NowPlaying with import alexa-layouts 1.7.0 + AlexaBackground(backgroundBlur) + AlexaProgressBar, behind AplEnhancedScreens default false; verify on the live Show (screen renders, art blurs, progress shows). Black screen = abort per the criterion.
2. Transport row (AlexaTransportControls or icon TouchWrappers) wired to SendEvent UserEvents through the EXISTING AplUserEventHandler (bonus: taps bypass the default-music-service arbitration that eats voice avanti).
3. AlexaTextList + primaryAction for disambiguation (tap-to-choose).
4. AlexaPaginatedList for chapter/episode picking.
5. Optional polish: LongPress gestures (favorites), AlexaRating, home-screen widget + data store.

Constraints (verified in the research): no push updates outside a request (re-render at track boundaries only, which JF-623 already does); documents die with the session; the Echo's own full-screen player takes over unless we render.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPIKE GATE first: a single NowPlaying render with the alexa-layouts 1.7.0 import (AlexaBackground backgroundBlur + AlexaProgressBar) verified ON-DEVICE to render (no black screen) before ANY other component adoption; the spike runs behind a new feature flag defaulting OFF
- [ ] #2 All enhanced screens behind a dedicated config flag (e.g. AplEnhancedScreens, default false): flag off = today's hand-rolled documents byte-identical; the feature is never in the release critical path
- [ ] #3 Touch wiring (transport buttons, tappable lists) rides the existing AplUserEventHandler chain and the JF-623 auto-attach chokepoint; no new handler architecture
- [ ] #4 Abort criterion documented in the task: any silent rendering failure (black screen, missing document) on the live 1024x600 hub = revert the specific screen to the hand-rolled template immediately, file the incompatibility, and do not retry without a documented platform change
- [ ] #5 Each adopted component lands as its own commit with its own device verification (one screen per step, never a batch)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-24 spike shipped (commit e3b5ed08): AplEnhancedScreens flag added (default false, PATCH-whitelisted), the enhanced NowPlaying template carries import alexa-layouts 1.7.0 + AlexaBackground(backgroundBlur) + AlexaProgressBar, transport row and datasources deliberately untouched to isolate the compatibility question. Deployed to minix, flag ON. RECON finding: the transport TouchWrappers (prev/pause/next) and their AplUserEventHandler routing already exist from the JF-582 era - the task's touch scope (plan items 2) is mostly DONE already; remaining device items are the tappable lists (items 3-4). AWAITING: the on-device spike gate check (one music play, screen must render with blurred art and progress bar, no black screen).

2026-09-24 spike gate PASSED (round 2, commit 92e00930): the enhanced document renders on the live Show, no black screen. Round-1 black screen root cause: I passed backgroundOverlayColor (a parameter that exists on AlexaPaginatedList, NOT on AlexaBackground); the documented AlexaBackground scrim knobs are colorOverlay/overlayGradient/overlayNoise, all Boolean. Verdict on the load-bearing history: the 2026-05-10 incompatibility does NOT reproduce with layouts 1.7.0 + document version 1.7 - the import is usable on-device. Paolo notes the enhanced screen LOOKS like the old one: expected and by design (the spike changed exactly background dimming->blur and the hand-made progress Frame->AlexaProgressBar, both visually subtle); the visibly-different upgrades (official transport states, tappable lists, ratings) are the next plan items. Flag left ON on minix.

2026-09-24 rounds 3-5 on the live bar, honest stop: (r3) real values threaded - static fill at render time only (no push channel); (r4) elapsedTime data-binding - INERT, bindings do not re-evaluate on document-clock ticks (my misread of the docs); (r5) the documented tick handler (handleTick + SetValue on npProgressBar, minimumDelay 1000) - deployed, VERIFIED ON THE WIRE in the 15:25 response (handleTick + id present), and the device still shows a non-advancing bar (Paolo: 'same bar as before'). Three rounds matching the documentation without visible movement = stop per discipline; next diagnostic requires SEEING the render (APL Authoring Tool with Paolo's logged-in session in the instrumented browser - offered), not another blind deploy. Alternative unexplored: handleTick at the DOCUMENT level (top-level APL handleTick property) instead of component-level - one shape variant worth testing inside the tool, not on-device. Sub-item PARKED pending either the tool session or a decision to ship the bar as static-at-render (the old hand-rolled bar was equally static; nothing regressed). The bar sweep seen in round 2 (0/0 degenerate) was actually the most visible animation the bar ever had.

2026-09-24 rounds 6-8, the full map (bar sub-item CLOSED as platform-unattainable during music playback; task continues on owned surfaces): (r6) root cause of r5 inertness found in the docs property table - the tick handler object REQUIRES a commands array; r5 had SetValue props directly on the handler = malformed, silently ignored. Reshaped; the web simulator (Paolo logged into the dev console in the instrumented browser; Device Display checkbox on) PROVED the corrected handler runs: the 'tick NNNN' probe text grew live in the Amazon web runtime. (r7) Paolo revealed the time line has NEVER been visible on the device across all rounds -> the surface he watches is not ours. Device Log shows the device synthesizing CardRenderer.PlayerInfo ~1s after PlaybackStarted; live card-drop experiment (no StandardCard in APL-carrying responses, verified on the wire) changed NOTHING -> the native player builds from AudioItem.Metadata alone and covers our document for the whole playback. Stream headers verified PERFECT on direct+public paths (Content-Length/Accept-Ranges/audio/mpeg) so the native sweep is not a headers defect; the determinate scrubber is MSAPI-only (JF-292/JF-561). Reverted the card-drop, removed the probe, KEPT the documented-shape tick handler (lights up in any visible window: launch flash, post-pause resurface - untested). SO 50622919 corroborates the flash-then-native behavior. CLAUDE.md Key Gotchas now carries the full constraint. NEXT (plan item 3): AlexaTextList + primaryAction tappable disambiguation lists - a surface we own (no native player arbitration), real user value (tap instead of 'il secondo'). The one remaining live-bar option for music, considered and rejected: the audiobook video-audio pipeline (real seek bar) costs queue/radio/gapless/album-art - documented here so the tradeoff is on record if Paolo ever wants an opt-in 'seek mode'.
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
