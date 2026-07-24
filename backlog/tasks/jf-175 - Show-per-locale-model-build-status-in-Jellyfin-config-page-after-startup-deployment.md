---
id: JF-175
title: >-
  Show per-locale model build status in Jellyfin config page after startup
  deployment
status: Done
assignee: []
created_date: '2026-05-17 16:03'
updated_date: '2026-05-17 16:28'
labels:
  - ux
  - models
  - feedback
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After SkillStartup deploys embedded models, capture per-locale SMAPI build results and display them in the plugin config page so failures aren't invisible.

**Problem**: When SMAPI rejects a model (empty type, unsupported feature), the failure only appears on the Amazon developer console. The Jellyfin config page shows no indication that a locale is broken. Users discover the problem only when the skill doesn't respond in that locale.

**Implementation plan**:

1. **Add per-locale model status to `PluginConfiguration`**:
   - Replace `LastModelDeployStatus` (single string) with `Dictionary<string, LocaleModelStatus>` keyed by locale
   - `LocaleModelStatus` record: `Status` (Succeeded/Failed/Pending), `LastUpdated` (DateTime), `Error` (string?), `Source` (Embedded/Custom)

2. **Extend `SkillStartup` to capture build results**:
   - After `CreateSkillAsync()` / `UpdateSkillAsync()`, call `smapi.WaitForSkillStatusAsync()` (already exists in `ModelDeploymentManager`)
   - Parse per-locale build status from SMAPI response
   - Store results in `PluginConfiguration.LocaleModelStatuses`
   - Log errors at WARNING level per-locale

3. **Add API endpoint for locale model status**:
   - `GET /api/custom-model/locale-statuses` → returns `Dictionary<string, LocaleModelStatus>`
   - Or extend existing `GET /api/custom-model/status` to include per-locale breakdown

4. **Update `config.html` to display per-locale status**:
   - Add a "Model Status" section showing a table of locales with status indicators
   - Green checkmark for SUCCEEDED, red X for FAILED with error message, gray for not yet deployed
   - Show last updated timestamp per locale
   - Click to expand error details

5. **Migration**: Convert existing `LastModelDeployStatus` string to the new structure on first load.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PluginConfiguration stores per-locale model build status (Succeeded/Failed with error message)
- [ ] #2 SkillStartup captures and stores SMAPI build results after deploying models
- [ ] #3 Config page shows per-locale status table with SUCCEEDED/FAILED indicators
- [ ] #4 Failed locales show the SMAPI error message (e.g. 'Custom type AudiobookTitle is empty')
- [ ] #5 Existing single-status display continues to work during migration
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Backend: LocaleModelStatus record in PluginConfiguration, CaptureLocaleModelStatusesAsync in SkillStartup, extended status API endpoint. Frontend: per-locale status table in config.html with ✓/✗ icons and truncated error tooltips.
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
