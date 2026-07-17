---
id: JF-328
title: >-
  UX: Onboarding wizard + end-user "is my skill working?" health panel in config
  UI
status: To Do
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-07-13 20:17'
labels:
  - feature
  - ux
  - onboarding
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Configuration/config.html
  - Jellyfin.Plugin.AlexaSkill/Controller/DiagnosticsController.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Setup today is a dense 1552-line single `config.html` with ~7 accordion sections — powerful but expert-oriented. There is a Simulator and a health/diagnostics controller but no guided first-run flow and no plain "is my skill working?" panel (functional review 2026-07-12). Given the plugin's documented pain that skill IDs churn on config wipes, onboarding/verification is the #1 support burden.

Deliver:
1. A guided first-run wizard walking through: server reachable → LWA client ID/secret → create skill via SMAPI → account linking → deploy models, with clear success/failure at each step (reuse existing endpoints).
2. An end-user health panel showing plain-language status: account-linking status, last successful model deploy, last Alexa request seen, current skill ID, connectivity to Jellyfin. Surface the existing JellyfinConnectivityChecker/diagnostics data.

Keep it inside the existing admin config page (RequiresElevation). This is a UI/UX task — build a rendered mockup and verify it visually before finalizing (per global manual: serve over http, screenshot).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A first-run wizard guides the admin through server/LWA/skill-creation/account-linking/model-deploy with per-step success/failure feedback
- [ ] #2 A health panel shows plain-language status: account-linking, last model deploy, last request seen, current skill ID, Jellyfin connectivity
- [ ] #3 The wizard and panel reuse existing diagnostics/health/simulator endpoints (no new backend duplication where avoidable)
- [ ] #4 The layout is verified via a rendered screenshot before completion (not ASCII)
- [ ] #5 No secrets (tokens/client secret) are exposed in the health panel output
- [ ] #6 Existing advanced/accordion config remains available for power users
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
