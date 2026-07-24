---
id: JF-158
title: 'Feature: URL-based interaction model override with SMAPI deploy'
status: Done
assignee: []
created_date: '2026-05-16 08:44'
updated_date: '2026-05-16 16:10'
labels:
  - feature
  - smapi
  - interaction-model
  - config
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Overview

Allow users to specify a URL pointing to a custom interaction model JSON, which the plugin deploys via SMAPI to the skill's development stage. This lets power users experiment with different utterances and intents without cloning the project or maintaining a fork.

## Context

- The skill is developer-only, shared within a household — no risk of affecting unrelated users
- The plugin already has SMAPI credentials configured for catalog/manifest management
- Interaction models are per-locale JSON files that define the NLU (utterances, intents, slots)
- SMAPI model builds take ~15-30s after deployment
- Currently, 17 locale models are baked into the codebase at `Alexa/InteractionModel/model_*.json`

## Architecture

### Data Flow
1. User provides a URL in the plugin config page (one per locale, or a single URL with locale path convention)
2. Plugin fetches the JSON from the URL
3. Plugin validates the model structure (basic schema check)
4. Plugin deploys via SMAPI `set-interaction-model` using existing credentials
5. SMAPI builds the model (15-30s)
6. Plugin polls `get-skill-status` until build completes
7. New model is active on the skill's development stage

### Config UI Changes
- New section in `Configuration/config.html`: "Custom Interaction Model"
- Per-locale URL fields (or a single URL with `{locale}` placeholder)
- **"Deploy Model" button** that:
  - Fetches the JSON from the configured URL
  - Validates basic structure
  - Deploys via SMAPI
  - Shows build status (spinner → success/failure)
  - Displays a link to Alexa developer console for testing
- "Restore Default" button to revert to the built-in model
- Status indicator: last deploy timestamp, build status, current model source (default/custom)

### Backend Changes

1. **New config properties** in `PluginConfiguration.cs`:
   - `CustomModelUrl` (string, nullable) — URL template for custom models
   - `CustomModelEnabled` (bool) — toggle for the feature
   - `LastModelDeployTime` (DateTime?) — tracking
   - `LastModelDeployStatus` (string?) — tracking

2. **New API endpoint** in the controller:
   - `POST /CustomModel/deploy` — triggers fetch + SMAPI deploy
   - `GET /CustomModel/status` — returns current deploy status
   - `POST /CustomModel/restore` — reverts to built-in model

3. **SMAPI integration** (extend existing `CatalogManager` or new `ModelDeploymentManager`):
   - `FetchModelJson(string url)` — HTTP GET with timeout/validation
   - `DeployInteractionModel(string locale, string modelJson)` — SMAPI `set-interaction-model`
   - `GetDeploymentStatus()` — SMAPI `get-skill-status` polling
   - `RestoreDefaultModel(string locale)` — deploy built-in model from `Alexa/InteractionModel/`

4. **Model validation** before deploy:
   - Check JSON is valid
   - Verify required fields: `interactionModel.languageModel.intents[]`, `interactionModel.languageModel.invocationName`
   - Warn (but allow) if intent count differs significantly from built-in model

### Merge vs Replace Strategy
Support both modes:
- **Replace** (default): Use the external model as-is. Simpler, user has full control.
- **Merge** (advanced): Overlay custom utterances onto the built-in model. Only add/override specific intents while keeping the base. More complex but safer.

For V1, start with **Replace only**. Add merge as a follow-up if users need it.

### UX Considerations
- The "Deploy Model" button should show clear feedback: fetching → validating → deploying → building → done
- If the SMAPI credentials aren't configured, show a helpful error with setup instructions
- The build takes 15-30s — use server-sent events or polling to update the UI
- Show the current model source clearly so users know if they're on default or custom

## Implementation Plan

1. Add config properties to `PluginConfiguration.cs`
2. Create `ModelDeploymentManager` class with SMAPI integration
3. Add API endpoints to the plugin controller
4. Update `config.html` with the new UI section
5. Add locale response strings for any voice feedback (if needed)
6. Write unit tests for model validation and deployment logic
7. Manual testing with real SMAPI credentials

## Acceptance Criteria
- [ ] User can configure a URL in the plugin settings
- [ ] "Deploy Model" button fetches, validates, and deploys the model via SMAPI
- [ ] Build status is shown in the UI (success/failure)
- [ ] "Restore Default" reverts to the built-in model
- [ ] Graceful error handling for invalid URLs, bad JSON, missing SMAPI creds
- [ ] Unit tests for validation and deployment logic
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented URL-based interaction model override with SMAPI deploy. Backend: ModelDeploymentManager with FetchModelJson, ValidateModelJson, DeployCustomModel, RestoreDefaultModel. Config: 5 new properties (CustomModelUrl, CustomModelLocale, CustomModelEnabled, LastModelDeployTime, LastModelDeployStatus). API: 3 endpoints (deploy, restore, status) in ConfigurationController. UI: accordion section with URL input, locale selector, Deploy/Restore buttons, status display, confirmation modal. Tests: 15 unit tests for validation and embedded model loading. Critical fixes applied: replaced broken temp file deserialization with direct JSON, threaded CancellationToken through async calls, reused SmapiManagement.WaitForSkillStatusAsync, extracted controller validation helper. 1486 tests pass, 0 warnings.
<!-- SECTION:FINAL_SUMMARY:END -->
