---
id: JF-265
title: >-
  FindSong multi-turn: short reply "family" misrouted to list/browse handler
  instead of song search
status: Done
assignee: []
created_date: '2026-06-06 13:40'
updated_date: '2026-06-06 15:10'
labels:
  - bug
  - findsong
  - conversation
  - session
  - it-IT
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Conversation flow bug in FindSongIntent multi-turn dialog.

**Reproduction:**
1. "Alexa, chiedi a mia collezione sto cercando una canzone dei beirut"
2. Skill prompts for the song name
3. User replies "family" (short single-word response)
4. Skill responds: "Non ho una lista precedente da continuare. Prova a navigare di nuovo."

**Expected:** "family" should be treated as the song title in the ongoing FindSong conversation, searching for a Beirut song called "family".

**Actual:** The short response "family" gets misrouted to a different intent (likely ShowMoreIntent or BrowseLibraryIntent) which interprets it as a navigation/continuation command. The error message "Non ho una lista precedente da continuare" comes from a list/browse handler, not FindSong.

**Root cause hypothesis:** The FindSong multi-turn conversation state (session attributes) may not be preserved correctly, or the short utterance "family" gets NLU-routed to a different intent that doesn't check for active FindSong session state.

**Investigation needed:**
1. Check how FindSongIntent stores conversation state in session attributes
2. Check if ShowMoreIntent or other handlers short-circuit without checking for active FindSong session
3. Check if short single-word responses get misrouted by Alexa's NLU
4. Check jellyfin logs for the actual intent that was routed when user said "family"

**Severity:** High — breaks the core FindSong conversational flow, making it unreliable for short song names.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed FindSong multi-turn dialog misrouting for short replies.

**Root cause:** Alexa's NLU routes short single-word replies like "family" to wrong intents (ShowMoreIntent, BrowseLibraryIntent) instead of FindSongIntent. The handler pipeline only dispatched to handlers whose CanHandle() matched the NLU-routed intent, so FindSongIntentHandler never saw the request.

**Fix (two layers):**
1. **Controller-level session routing** (AlexaSkillController.cs): When Alexa session contains `FindSongSessionData`, bypass normal CanHandle() iteration and route directly to FindSongIntentHandler regardless of NLU intent classification.
2. **Handler-level slot fallback** (FindSongIntentHandler.cs): Added `GetAnySlotValue()` to extract text from any available slot when expected slots (titleKeywords, musician) aren't present in the misrouted intent. Applied as fallback in AwaitingKeywords, AwaitingArtist, and Disambiguating states.

**Tests:** 8 new tests covering cross-intent slot extraction, ShowMoreIntent/BrowseLibraryIntent during active sessions, and GetAnySlotValue edge cases. All 2232 tests pass.

**Commit:** bd3d26b
<!-- SECTION:FINAL_SUMMARY:END -->
