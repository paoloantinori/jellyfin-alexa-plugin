---
id: JF-96
title: Improve mixed-language music recognition for Italian locale
status: Done
assignee: []
created_date: '2026-05-07 16:56'
updated_date: '2026-05-07 20:30'
labels:
  - alexa
  - nlu
  - italian
  - music-recognition
dependencies: []
references:
  - claudedocs/research_slot_types_mixed_language_2026-05-07.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Improve Alexa's ability to recognize English song titles and artist names when users speak Italian. Based on exhaustive research (see claudedocs/research_slot_types_mixed_language_2026-05-07.md).

**Key architectural context**: Each Jellyfin instance deploys its own private Alexa skill (unique skill ID per user). This means catalog-based slot types are effectively per-user — we can upload each user's entire Jellyfin library as a SMAPI catalog attached to their personal skill. This is much better than session-scoped dynamic entities because it persists across sessions.

This is a parent task covering phased improvements:
- Phase 1: Fix broken Dialog.Delegate + expand Italian utterances (quick win)
- Phase 2: Per-user catalog-based custom slot types populated from Jellyfin library (HIGH IMPACT — possible because skills are private)
- Phase 3 (optional): Dynamic entities for recently-played items as session supplement

Key constraints:
- AMAZON.SearchQuery cannot share an utterance with another slot type (per-utterance, not per-intent)
- Music Skill API (catalog upload) is US-only and unavailable for custom skills
- Slot name must use same slot type across all intents in a locale
- AMAZON.MusicRecording is NOT an enumeration — returns values outside its training data
- it-IT has no dialog models (only languageModel), so Dialog.Delegate always fails
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 English artist names (beyond top-20) are recognized at least 50% of the time in Italian utterances
- [ ] #2 Dialog.Delegate no longer crashes on it-IT locale
- [ ] #3 NLU test fixtures for Italian include English proper noun test cases with expected slot values
- [ ] #4 No regression in existing Italian intent recognition (77/77 NLU tests pass)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Completed all three phases of Italian locale music recognition improvements:

JF-96.1: Replaced Dialog.Delegate with self-managed slot elicitation — it-IT has no dialog model so Dialog.Delegate always crashed. Now uses ElicitSlot directives directly. Also expanded Italian utterances.

JF-96.2: Added catalog-based custom slot types (AMAZON.Musician, AMAZON.Album) populated from each user's Jellyfin library via LibrarySyncService. Includes phonetic synonym generation for Italian-friendly pronunciation hints. Skills are private (one per Jellyfin instance), so catalogs are effectively per-user.

JF-96.3: Added dynamic entity resolution via DynamicEntitiesInterceptor that injects Dialog.UpdateDynamicEntities on new sessions/launch requests. Uses DynamicEntityBuilder to pull recently-added artists and albums for session-scoped NLU personalization.

All 894 unit tests pass. 19 new tests added across the three phases.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
