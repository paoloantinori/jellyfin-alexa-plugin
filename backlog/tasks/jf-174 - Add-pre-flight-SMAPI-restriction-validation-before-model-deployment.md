---
id: JF-174
title: Add pre-flight SMAPI restriction validation before model deployment
status: Done
assignee: []
created_date: '2026-05-17 16:03'
updated_date: '2026-05-17 16:28'
labels:
  - safety-net
  - models
  - validation
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Validate interaction models against known SMAPI restrictions before deploying, catching issues that Amazon would reject (empty custom types, locale-unsupported features like fallbackIntentSensitivity).

**Problem**: We shipped models with empty `AudiobookTitle` slot types and `fallbackIntentSensitivity` in locales that don't support it (only en-*/de-DE do). SMAPI rejects these silently — the only signal is a build failure on the Amazon developer console, which nobody checks routinely.

**Implementation plan**:

1. **Extend `ModelDeploymentManager.ValidateModelJson()`** (or add a new `ValidateSMAPIRestrictions()` method) that checks:
   - Custom slot types must have ≥1 value (SMAPI rejects empty types)
   - `fallbackIntentSensitivity` only valid for `en-*` and `de-DE` locales
   - Add a `locale` parameter so locale-specific rules can be applied

2. **Wire into both deployment paths**:
   - `ModelDeploymentManager.DeployCustomModelAsync()` — validate before SMAPI call, return early with clear error if invalid
   - `SkillStartup` — validate embedded models before pushing, log warnings per-locale
   - `ConfigurationController` custom model deploy endpoint — return validation errors in API response

3. **Extend `validate_interaction_models.py`** (already done for empty types and sensitivity — verify coverage is complete)

4. **Add unit tests** for the restriction validator covering:
   - Empty custom type → fail
   - Sensitivity in non-en/de locale → fail
   - Sensitivity in en-US → pass
   - All valid → pass
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Pre-flight validation catches empty custom slot types before SMAPI submission
- [ ] #2 Pre-flight validation catches fallbackIntentSensitivity in non-en/de locales
- [ ] #3 SkillStartup logs per-locale validation warnings without blocking startup
- [ ] #4 ConfigurationController returns validation errors in deploy API response
- [ ] #5 validate_interaction_models.py already covers both checks (verify)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added ValidateSMAPIRestrictions to ModelDeploymentManager — checks fallbackIntentSensitivity in non-en/de locales and empty custom slot types before SMAPI deployment. Wired into all three deploy paths (startup, custom model, controller). 15 unit tests covering edge cases.
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
