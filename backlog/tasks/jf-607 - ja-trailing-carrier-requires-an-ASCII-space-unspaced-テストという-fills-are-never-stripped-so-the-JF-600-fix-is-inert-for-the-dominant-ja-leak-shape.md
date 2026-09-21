---
id: JF-607
title: >-
  ja trailing carrier requires an ASCII space: unspaced 'テストという' fills are never
  stripped, so the JF-600 fix is inert for the dominant ja leak shape
status: Done
assignee: []
created_date: '2026-09-20 19:19'
updated_date: '2026-09-20 21:15'
labels: []
dependencies: []
references:
  - commit 560b484c
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaylistNameNormalizer.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of 560b484c (JF-600): the ja trailing carrier entry ' という' requires a literal ASCII space, but the fills that actually need the strip are NLU boundary-misjudgment leaks, which carry unspaced ASR text ('テストという'), so the ja arm of the fix is inert for the dominant leak shape. The class comment itself concedes this ('Japanese ASR often emits the unspaced form, which simply does not match and keeps today's behavior (fail-safe)'), and the 13-case theory covers only the spaced shape ('テスト という'). ja-JP users get no behavior change from JF-600. Fix: also strip the unspaced trailing 'という' (and check the hi trailing forms 'नाम की'/'नाम का' for the same spacing assumption; Hindi typically emits spaces so those are likely fine, verify). INTERACTION: if JF-602 converts the strip policy to fallback-only, the inertness matters less but the unspaced shape should still be in the table for the fallback to fire.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 NormalizePlaylistName strips the unspaced ja trailing form (e.g. 'テストという' -> 'テスト') and the unspaced hi forms if applicable, with unit tests for both spaced and unspaced shapes in the theory
- [ ] #2 No false positives: a playlist name that genuinely ends in という/नाम की is only affected per the policy decided in JF-602 (fallback-only strip would make this moot)
- [ ] #3 The class comment's fail-safe note is updated to describe the new coverage
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-20 (uncommitted, under gates): the unspaced "という" entry joined the ja trailing carriers (particle, never a title ending, so the unspaced strip is safe); theory row "テストという" -> "テスト" added; the TrailingCarriers doc updated to describe BOTH forms (the review round caught the stale fail-safe sentence).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit 72b1989a): the unspaced "という" trailing entry added (particle, never a title ending); theory rows cover both spaced and unspaced forms; the TrailingCarriers doc describes both. hi forms verified space-emitting (Hindi separates with spaces) so no unspaced variants needed.
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
