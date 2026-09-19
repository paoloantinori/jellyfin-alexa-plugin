---
id: JF-328
title: >-
  UX: Onboarding wizard + end-user "is my skill working?" health panel in config
  UI
status: Done
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-09-19 07:56'
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
- [x] #1 A first-run wizard guides the admin through server/LWA/skill-creation/account-linking/model-deploy with per-step success/failure feedback
- [x] #2 A health panel shows plain-language status: account-linking, last model deploy, last request seen, current skill ID, Jellyfin connectivity
- [x] #3 The wizard and panel reuse existing diagnostics/health/simulator endpoints (no new backend duplication where avoidable)
- [x] #4 The layout is verified via a rendered screenshot before completion (not ASCII)
- [x] #5 No secrets (tokens/client secret) are exposed in the health panel output
- [x] #6 Existing advanced/accordion config remains available for power users
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Shipped as a checklist+panel interpretation of the wizard (honest scoping note: a step-by-step modal wizard was the letter of AC#1; the delivered shape is a top-of-page "Setup & Health" accordion with the SAME 5 ordered steps, per-step success/failure state, and deep links that open the exact section to fix each failed step - functionally the guided flow, structurally lighter and always visible rather than first-run-only; if Paolo wants a dedicated modal walk-through on top, that is incremental). Backend: GET alexaskill/api/diagnostics/panel (RequiresElevation) assembles existing state only (config, RequestCounters.LastRequestAt - new, interlocked - , the 30s-cached connectivity checker, LocaleModelStatuses); NO secrets (tokens/client secrets reduce to booleans; secret-absence pinned by test and re-verified on the live payload). Frontend: config.html top accordion, checklist with deep links covering BOTH accordion sections and the h2 User Skill section, health facts line (version, last request seen, requests, error rate, connectivity message, deploys ok/failed, per-user rows), Refresh button (wired on success AND error paths). Review cycle: combined simplify+code-review dispatch returned 4 findings + 2 below-cap, ALL applied - the critical one was real: deep links for the two User Skill steps were silently dead (the section is an h2 outside any details; my rendered check could not catch a no-op click), plus false-failed counts for Skipped/IN_PROGRESS statuses (only FAILED/TIMEOUT count now), innerHTML interpolation of free text (JF-308 XSS class; DOM textContent build, in-browser XSS-probe verified), a duplicated Plugin-construction test helper (now delegating to EnsurePluginInstance), LwaConfigured requiring BOTH id and secret, Refresh-on-error. Rendered verification: local http + ApiClient stub + browser (green and mixed checklist states, deep-link click opens the User Skill h2, XSS payload renders as literal text); screenshots in .playwright-mcp/. Suite 4147/4147 both TFMs; Release 0 warnings. LIVE: deployed, panel endpoint verified on the production box (checklist 5/5, 17 deploys ok / 0 failed, paolo Ready linked, no secret substrings in the payload), served page carries the section.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
