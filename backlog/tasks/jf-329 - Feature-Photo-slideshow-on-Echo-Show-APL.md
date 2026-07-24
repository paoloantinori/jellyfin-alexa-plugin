---
id: JF-329
title: 'Feature: Photo slideshow on Echo Show (APL)'
status: To Do
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-07-13 20:18'
labels:
  - feature
  - apl
  - photos
milestone: m-10
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Apl/
  - Jellyfin.Plugin.AlexaSkill/Alexa/Interface/
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
There is no `Photo` handling anywhere in the plugin (functional review 2026-07-12). Jellyfin supports photo libraries and the Echo Show is a natural surface. "Show my {album} photos" → an APL image sequence/slideshow. Niche but differentiating and visual, and it avoids the AudioPlayer platform limits entirely (pure APL, no default-music-service competition).

New intent: resolve a Jellyfin photo album/library folder for the linked user (respect library gating), build an APL slideshow directive cycling images via the existing AplHelper patterns, and handle Echo Show capability detection (fall back to a spoken "this needs a screen" on audio-only devices). Handler + IntentNames + samples (17 locales, it-IT YAML) + response strings + tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can request a photo album/library by name and see an APL slideshow on an Echo Show
- [ ] #2 Photo selection respects per-user library gating
- [ ] #3 On audio-only devices the skill responds gracefully that a screen is required (capability detection)
- [ ] #4 Slideshow uses existing AplHelper patterns and valid APL (passes validate_apl.py)
- [ ] #5 Samples + response strings across all 17 locales; unit tests included
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
