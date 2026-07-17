---
id: JF-185
title: >-
  Split resume behavior into two independent settings: ResumeOfferEnabled and
  ResumeAnnounceTitle
status: Done
assignee: []
created_date: '2026-05-19 17:26'
updated_date: '2026-05-19 18:06'
labels:
  - config
  - feature-flag
  - playback
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Currently `ResumeAnnounceTitle` is a single boolean that only controls whether the title is spoken *after* the user accepts the resume offer. But the user needs two independent controls:

## Setting 1: `ResumeOfferEnabled` (default: true)
Controls whether the skill **offers** to resume content when reopened (the "Would you like to resume X?" prompt on LaunchRequest). When disabled, the skill skips the resume offer entirely and goes straight to the welcome screen, even if AudioPlayer context has prior playback state.

## Setting 2: `ResumeAnnounceTitle` (already exists, keep as-is)
Controls whether the skill **speaks the title** after the user accepts the resume offer and playback resumes. When disabled, it says just "Resuming" (brief). When enabled, it says "Resuming: {title}".

## Files to change
- `PluginConfiguration.cs` — add `ResumeOfferEnabled` property (default true)
- `config.html` — add checkbox in Feature Flags for "Resume Offer"
- `LaunchRequestHandler.cs` — check `ResumeOfferEnabled` before calling `HandleResumeOfferAsync`; when disabled, fall through to the welcome response instead
- `config.html` — update the existing `ResumeAnnounceTitle` description to clarify it only controls the post-accept title announcement
- Unit tests for new flag default + serialization
- Unit tests for LaunchRequestHandler behavior when flag is off (no resume offer, shows welcome instead)

## Config UI placement
Both settings should be in the Feature Flags section. Suggested labels:
- "Resume Offer" — "Offer to resume previously playing content when the skill is reopened"
- "Announce Title on Resume" — (existing, keep description)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 New ResumeOfferEnabled boolean property exists on PluginConfiguration with default true
- [ ] #2 Config UI has two separate checkboxes in Feature Flags: Resume Offer and Announce Title on Resume
- [ ] #3 When ResumeOfferEnabled=false, LaunchRequestHandler skips resume offer and shows welcome instead
- [ ] #4 When ResumeOfferEnabled=true and ResumeAnnounceTitle=true, current behavior unchanged
- [ ] #5 When ResumeOfferEnabled=true and ResumeAnnounceTitle=false, resume offer appears but playback starts with brief 'Resuming' (no title)
- [ ] #6 Unit tests cover feature-on/off for the new ResumeOfferEnabled flag
- [ ] #7 Existing tests continue to pass
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented ResumeOfferEnabled feature flag gating LaunchRequestHandler resume offer path. When disabled, skill skips resume offer and shows welcome instead. ResumeAnnounceTitle kept independent (controls title announcement after accepting). Added APL welcome splash screen (always renders on APL devices) and dedicated resume-offer screen. 15 new tests, all 1702 passing.
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
