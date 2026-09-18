---
id: JF-592
title: 'ar-SA typo: ألبوبًا for ألبومًا (album) in CrossMediaArtistOffer(+Ssml)'
status: To Do
assignee: []
created_date: '2026-09-18 21:02'
labels:
  - bug
  - i18n
milestone: Polish
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pre-existing ar-SA typo found by the JF-590 code review: Jellyfin.Plugin.AlexaSkill/Alexa/Locale/ar-SA.json spells ألبوبًا (missing the م) instead of ألبومًا ("album") in both CrossMediaArtistOffer and CrossMediaArtistOfferSsml. The misspelling is spoken to Arabic users on the cross-media artist offer. Two-character fix; verify no other occurrence in the file.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 CrossMediaArtistOffer and CrossMediaArtistOfferSsml in ar-SA.json spell ألبومًا (album) correctly
- [ ] #2 No other occurrence of the misspelling ألبوبًا remains in ar-SA.json (grep clean)
- [ ] #3 Tests pass on both TFMs without --no-build
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
