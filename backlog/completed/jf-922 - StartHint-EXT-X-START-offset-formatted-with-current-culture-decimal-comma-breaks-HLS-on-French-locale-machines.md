---
id: JF-922
title: >-
  StartHint EXT-X-START offset formatted with current culture (decimal comma
  breaks HLS on French-locale machines)
status: Done
assignee: []
created_date: '2026-10-31 20:35'
updated_date: '2026-11-09 19:45'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Reported by Tergin87 on issue #43 (2026-10-31 18:59): two AudiobookPlaylistBuilderTests tests (BuildStartHintPlaylist_*) fail on a French-locale machine because the EXT-X-START offset is formatted with the current culture's decimal separator: "30,000" instead of "30.000" (the HLS attribute grammar requires a dot). They pass on en-US CI, which is why it never surfaced. Fix: format the offset with InvariantCulture at the formatting site in AudiobookPlaylistBuilder (grep for the StartHint offset formatting; likely ToString or string interpolation without CultureInfo). Add/adjust a test that pins the invariant dot under a comma-decimal culture if the harness allows setting CultureInfo.CurrentCulture.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed same-day as the filing in commit 6dac6538 (2026-10-31, riding the JF-920 revert): the StartHint EXT-X-START offset formats via string.Create(CultureInfo.InvariantCulture, ...) at AudiobookPlaylistBuilder.cs:82, and the fr-FR culture pin landed in Unit/AudiobookPlaylistBuilderTests.cs:276-280 (drives the builder under CurrentCulture=fr-FR and pins the literal '#EXT-X-START:TIME-OFFSET=30.000,PRECISE=YES' dot). Verified green in this session's two full suite runs (6154/6154 per TFM, 2026-11-09). The task had simply never been closed after the fix shipped; closed 2026-11-09 on that evidence. The reporter's French-machine reds were addressed by the same commit he reported against (he ran a pre-fix tree).
<!-- SECTION:FINAL_SUMMARY:END -->
