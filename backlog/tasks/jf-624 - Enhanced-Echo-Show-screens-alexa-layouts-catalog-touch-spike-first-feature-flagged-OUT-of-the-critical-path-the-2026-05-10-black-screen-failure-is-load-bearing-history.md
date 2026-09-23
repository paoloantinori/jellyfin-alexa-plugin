---
id: JF-624
title: >-
  Enhanced Echo Show screens (alexa-layouts catalog + touch) - spike-first,
  feature-flagged, OUT of the critical path (the 2026-05-10 black-screen failure
  is load-bearing history)
status: To Do
assignee: []
created_date: '2026-09-23 19:50'
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
