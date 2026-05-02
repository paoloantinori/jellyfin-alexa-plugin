---
id: JF-21
title: Add search disambiguation with Yes/No dialogue for multiple results
status: Done
assignee: []
created_date: '2026-05-01 06:21'
updated_date: '2026-05-01 20:56'
labels:
  - feature
  - ux
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
FEATURE from upstream Python project: When a search returns multiple results, cycle through top 3 matches asking "Would you like to hear X by Y?" with Yes/No responses.

Implementation:
- Add AMAZON.YesIntent and AMAZON.NoIntent handlers
- Store TopMatches list in session attributes (first match auto-plays, rest queued)
- When user says "No", play next match; "Yes" continues playing
- Applicable to: PlaySong, PlayAlbum, PlayArtistSongs, PlayVideo, PlayPlaylist handlers
- Currently these handlers just play the first match and discard the rest

Reference: infinityofspace/jellyfin_alexa_skill PR #12 (merged)
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Design Decisions
- **Disambiguation happens BEFORE playback**: When search returns multiple results, ask about each match sequentially. "Would you like to hear X?" → Yes plays it, No asks about next match.
- **Single result = auto-play** (unchanged behavior): No disambiguation needed.
- **Top 3 matches max**: Only cycle through the first 3 results.
- **Non-breaking signature change**: Add a virtual `HandleAsync` overload with `sessionAttributes` parameter that delegates to the original abstract method by default.

### Session Attributes Format
```
"disambig_matches": JSON array of [{"id":"guid","name":"Item Name"}]
"disambig_index": int (0-based index of current match)
"disambig_type": "song"|"album"|"artist"|"video"|"playlist"
```

### File Changes

**Foundation:**
1. `IntentNames.cs` — Add `AmazonYes`, `AmazonNo` constants
2. `en-US.json` / `it-IT.json` — Add disambiguation locale strings
3. `BaseHandler.cs` — Add `HandleRequestAsync` overload accepting `Session`, add virtual `HandleAsync` with sessionAttributes
4. `AlexaSkillController.cs` — Pass `req.Session` to handlers, register Yes/No handlers

**New Files:**
5. `DisambiguationHelper.cs` — Static utility for building/reading disambiguation state
6. `YesIntentHandler.cs` — Handle AMAZON.YesIntent during disambiguation
7. `NoIntentHandler.cs` — Handle AMAZON.NoIntent during disambiguation

**Modified Play Handlers (add disambiguation when results > 1):**
8. `PlaySongIntentHandler.cs`
9. `PlayAlbumIntentHandler.cs`
10. `PlayArtistSongsIntentHandler.cs`
11. `PlayVideoIntentHandler.cs`
12. `PlayPlaylistIntentHandler.cs`

**Tests:**
13. `YesIntentHandlerTests.cs`
14. `NoIntentHandlerTests.cs`
15. Update existing tests for disambiguation behavior
<!-- SECTION:PLAN:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented search disambiguation with Yes/No dialogue for multiple results.

**New files:**
- `DisambiguationHelper.cs` — Static utility for session attribute management with typed media type constants
- `YesIntentHandler.cs` — Handles AMAZON.YesIntent, plays confirmed match (song/album/artist/video/playlist)
- `NoIntentHandler.cs` — Handles AMAZON.NoIntent, advances to next match or reports none

**Modified files:**
- `BaseHandler.cs` — Added session-aware HandleRequestAsync overload + virtual HandleAsync with sessionAttributes (non-breaking)
- `AlexaSkillController.cs` — Passes req.Session to handlers, registers Yes/No handlers
- `IntentNames.cs` — Added AmazonYes/AmazonNo constants
- `en-US.json`, `it-IT.json` — Added disambiguation locale strings
- All 5 play handlers — Added multi-result detection with DisambiguationHelper.AskFirstMatch()

**Simplify fixes applied:**
- Replaced stringly-typed media types with DisambiguationHelper constants
- Fixed hardcoded "en-US" locale in YesIntentHandler error responses
- Removed async/await over synchronous Task.FromResult (eliminated unnecessary state machine)
- Removed unused constructor dependencies from NoIntentHandler
- Fixed redundant GetItemById in PlayPlaylist

**Tests:** 219 passed (34 new tests for DisambiguationHelper, YesIntentHandler, NoIntentHandler, and updated PlayVideo disambiguation test)
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 /simplify
<!-- DOD:END -->
