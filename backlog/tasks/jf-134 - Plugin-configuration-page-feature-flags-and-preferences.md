---
id: JF-134
title: 'Plugin configuration page: feature flags and preferences'
status: Done
assignee: []
created_date: '2026-05-12 15:16'
updated_date: '2026-05-12 20:31'
labels:
  - configuration
  - enhancement
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Expand the Jellyfin plugin configuration page (`Configuration/config.html` + `PluginConfiguration`) to expose feature flags and user-tunable preferences for the many capabilities that are currently hardcoded or absent from the config surface.

**Why**: The plugin now has ~55 intent handlers spanning radio mode, podcasts, live TV, sleep timer, queue management, APL visuals, gapless playback, and more — yet the config page only exposes server address, LWA credentials, fuzzy-match behavior, and simulator toggle. Administrators need a way to enable/disable feature groups and tune behavior without editing code.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Plugin config page has a dedicated 'Feature Flags' section with toggleable groups
- [x] #2 Plugin config page has a 'Playback Preferences' section with configurable defaults
- [x] #3 Plugin config page has a 'Search Preferences' section with tunable parameters
- [x] #4 All new config properties are persisted via PluginConfiguration and survive server restart
- [x] #5 Existing config page layout (server address, LWA, users) is preserved
- [x] #6 Configuration changes take effect without server restart (hot-reload via ConfigurationChanged)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## UX Architecture Decision

Based on research of 7 Jellyfin plugins (see `claudedocs/research_plugin_config_ux_patterns_2026-05-12.md`):

**Pattern: Accordion sections via `emby-collapse`** (from LDAP Auth plugin — closest match to our needs)

Stay with inline `<script>` + Emby web components. No build pipeline changes needed.

**Key patterns to adopt:**
- `emby-collapse` for section grouping (Feature Flags, Playback, Search, Content Access)
- `checkboxContainer-withDescription` for toggle switches with explanations
- Conditional visibility via `display:none/block` for dependent sections
- Standard load/save pattern: `ApiClient.getPluginConfiguration()` → populate → `updatePluginConfiguration()`

**Config page layout** (top to bottom):
1. General Configuration (existing, unchanged)
2. Feature Flags (emby-collapse, toggleable intent groups)
3. Playback Preferences (emby-collapse, checkboxes/number inputs/dropdowns)
4. Search Preferences (emby-collapse, sliders/number inputs/checkboxes)
5. Content Access (emby-collapse, media type toggles + collection allow/deny)
6. User Skill Configuration (existing, extended with per-user content access)

**Estimated scope**: ~300-400 additional lines in config.html, ~50-100 lines in PluginConfiguration.cs
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-134 was already substantially complete from prior work (JF-134.1, JF-134.2, JF-134.3 all Done). This session wired up the last remaining gap: AplVisualsEnabled was defined in config but never enforced. Added AplHelper.VisualsEnabled property and checked it in all 3 APL directive attachment sites (BaseHandler.BuildAudioPlayerResponse, MediaInfoIntentHandler.TryAttachNowPlayingCard, ListQueueIntentHandler). 3 new tests verify the flag works. All 1317 tests pass.
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
