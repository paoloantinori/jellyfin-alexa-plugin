---
id: JF-171
title: Fill 437 missing locale string keys across 15 locales
status: Done
assignee:
  - claude
created_date: '2026-05-17 13:46'
updated_date: '2026-05-17 15:11'
labels:
  - i18n
  - tech-debt
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
15 locales have 437 total missing response string keys compared to en-US (179 keys). Breakdown:

**~43 keys each** (de-DE, en-AU, en-CA, en-GB, en-IN, es-ES, es-MX, es-US, fr-CA, fr-FR):
Missing keys like AddedToQueue, AlbumsByArtistList, DidNotCatchArtistName, DidNotCatchDecade, DidNotCatchQueueItem, ElicitAlbumName, ElicitSeriesName, ElicitSongName, NowPlayingSsml, PlayNextConfirmed, QueueCleared, QueueEmpty, QueueList, RadioModeOff, RadioStarted, VoiceLearned, WelcomeSsml, WhoAmI, etc.

**1 key each** (ar-SA, hi-IN, ja-JP, nl-NL, pt-BR):
Missing FollowMeSuccessSsml only.

A missing key causes runtime KeyNotFoundException when ResponseStrings.Get() is called for that locale. Many of these keys are for intents the locales don't have yet (e.g., Radio, Queue), but some (like WelcomeSsml) are used unconditionally.

This is tracked in `scripts/locale_baseline.json` so CI doesn't block on pre-existing gaps.

Suggested approach: fix in batches by priority — first unconditional keys (WelcomeSsml, etc.), then per-intent keys when backporting each intent.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Plan: Fill 437 missing locale keys

### Phase 1: Easy locales (5 locales × 1 key each)
- ar-SA, hi-IN, ja-JP, nl-NL, pt-BR: Add FollowMeSuccessSsml

### Phase 2: English variants (4 locales × ~43 keys)
- en-AU, en-CA, en-GB, en-IN: Copy en-US values (same language)

### Phase 3: Non-English locales (6 locales × ~43 keys)
- de-DE: German translations
- es-ES, es-MX, es-US: Spanish translations
- fr-CA, fr-FR: French translations

### Phase 4: Validate
- Run validate_locales.py to confirm 0 gaps
- Update locale_baseline.json
- Run dotnet build to verify no breakage
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Progress
- All 437 missing locale keys filled across 15 locales
- 5 single-key locales (ar-SA, hi-IN, ja-JP, nl-NL, pt-BR): Added FollowMeSuccessSsml
- 4 English variants (en-AU, en-CA, en-GB, en-IN): Copied en-US values (no British spelling differences found)
- de-DE: 43 German translations added
- es-ES, es-US, es-MX: 44 Spanish translations each added (es-MX preserves 'claro' interjection)
- fr-FR, fr-CA: 43 French translations each added (fr-CA preserves 'allons-y' interjection)
- locale_baseline.json cleared to {} since no gaps remain
- validate_locales.py passes with 0 gaps
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Filled all 437 missing locale string keys across 15 locales:

- **5 single-key locales** (ar-SA, hi-IN, ja-JP, nl-NL, pt-BR): Added FollowMeSuccessSsml with localized interjections
- **4 English variants** (en-AU, en-CA, en-GB, en-IN): Copied en-US values (no British spelling differences found in existing keys)
- **de-DE**: 43 German translations for queue, radio, artist info, voice, SSML variants
- **es-ES, es-US, es-MX**: 44 Spanish translations each (es-MX preserves "claro" interjection vs "de acuerdo")
- **fr-FR, fr-CA**: 43 French translations each (fr-CA preserves "allons-y" interjection vs "c'est noté")
- Cleared `locale_baseline.json` to `{}` — no known gaps remain
- `validate_locales.py` passes with 0 gaps across all 17 locales
- Build + 1523 tests pass
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
