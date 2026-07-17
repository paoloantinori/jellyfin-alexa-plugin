---
id: JF-197
title: Multi-content resume with Jellyfin server-side progress tracking
status: Done
assignee:
  - claude
created_date: '2026-05-21 17:39'
updated_date: '2026-05-21 18:43'
labels: []
milestone: Resume Improvements
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ResumeIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ContinueWatchingIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/StartOverIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs
documentation:
  - >-
    Jellyfin IUserDataManager stores per-user, per-item PlaybackPositionTicks —
    the server-side source of truth for resume positions
  - >-
    ContinueWatchingIntentHandler.cs already demonstrates the query pattern:
    InternalItemsQuery with DatePlayed ordering, filtering by
    PlaybackPositionTicks > 0 and Played = false
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

When users alternate between content types (audiobook → music → video → book), each new play overwrites the single-slot DeviceQueue, losing the previous content's resume state. Jellyfin's server already stores `UserData.PlaybackPositionTicks` per item — but the plugin's ResumeIntentHandler never queries it, and play handlers always start from the beginning regardless of existing progress.

## Architecture

The change spans three layers:

1. **ResumeIntentHandler enhancement**: Add a 4th fallback tier that queries Jellyfin for the most recently played item with `PlaybackPositionTicks > 0` when no AudioPlayer/DeviceQueue state exists. This makes "resume" work across content switches without any play-handler changes.

2. **Play handler resume awareness**: When a play handler resolves a specific item that already has server-side progress, offer to resume from that position (or auto-resume for books/video, ask for music). Books must resume from the correct chapter track + offset.

3. **Enhanced StartOverIntent**: Make `AMAZON.StartOverIntent` work beyond the currently-playing item — when the user says "start over" or "play from the beginning" combined with a content request, clear the Jellyfin progress data and play from position 0. Critical for music (replaying a song from the top), important for books and video too.

## Content Type Behavior

| Content Type | Resume on Play | StartOver Scope |
|---|---|---|
| **Audiobook** | Auto-resume from correct chapter + position (user expects continuity) | Current item or named book |
| **Video** (Movie/Episode) | Auto-resume from saved position | Current item or named video |
| **Music** (Artist/Album/Song) | Auto-resume from beginning (songs are short, position rarely matters) BUT resume the queue/album from where they left off | Current song or named content |

## Key Files
- `ResumeIntentHandler.cs` — add Jellyfin server-side query fallback
- `ContinueWatchingIntentHandler.cs` — reference for the Jellyfin progress query pattern
- `PlayBookIntentHandler.cs` — add chapter + position resume
- `PlayArtistSongsIntentHandler.cs` — add queue position resume  
- `PlayAlbumIntentHandler.cs` — add track position resume
- `PlayVideoIntentHandler.cs` — add video position resume
- `StartOverIntentHandler.cs` — enhance for non-playing items + clear progress
- `BaseHandler.cs` — add shared `GetResumePosition()` helper
- `DeviceQueueManager.cs` — no changes needed (Jellyfin is the source of truth)
- 17 locale JSON files — new response strings
- 17 interaction model JSONs — new utterances for restart intent if needed
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 ResumeIntentHandler falls back to Jellyfin server-side progress when no AudioPlayer/DeviceQueue state exists, finding the most recently played item with PlaybackPositionTicks > 0 across audio, book, and video content types
- [ ] #2 PlayBookIntentHandler checks for existing progress on the resolved audiobook and auto-resumes from the correct chapter track + offset position
- [ ] #3 PlayVideoIntentHandler checks for existing progress and auto-resumes from the saved position using VideoApp with offset
- [ ] #4 Play handlers for music (artist, album, playlist) resume the queue from the last track the user was listening to, but each track starts from position 0
- [ ] #5 StartOverIntentHandler works for both currently-playing items AND the last-played item (when nothing is currently playing), clearing Jellyfin PlaybackPositionTicks before playing from position 0
- [ ] #6 User can explicitly request 'play from the beginning' for any content type, which clears server-side progress and starts fresh
- [ ] #7 All new behavior is covered by unit tests with mocked IUserDataManager and ILibraryManager
- [ ] #8 17 locale files updated with any new response strings
- [ ] #9 Interaction models updated if new utterances or slot types are needed
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
## Implementation Plan

### Phase 1: Shared Resume Helper (BaseHandler)
Add a protected helper method to `BaseHandler` that queries Jellyfin for an item's server-side resume position. This avoids duplicating the IUserDataManager query logic across all play handlers.

**New dependency**: `IUserDataManager` and `IUserManager` injection into handlers that need resume awareness (already available in ContinueWatchingIntentHandler).

**New method on BaseHandler**:
```csharp
protected (BaseItem? item, long positionTicks) FindLastPlayedItemWithProgress(
    JellyfinUser user, params BaseItemKind[] contentTypes)
```

### Phase 2: ResumeIntentHandler — 4th Fallback Tier
When `ResumeIntentHandler` finds no AudioPlayer token, no session state, and no DeviceQueue state, instead of returning "NoMediaPlaying", query Jellyfin for the most recently played item with progress across all content types (audio, audioBook, movie, episode).

**Change**: Inject `ILibraryManager`, `IUserManager`, `IUserDataManager` into `ResumeIntentHandler`. Add the Jellyfin fallback as tier 4 after the existing three tiers.

**Behavior**: "Alexa, resume" → if nothing in session/device state → find last played audiobook chapter 7 at 2:34:00 → resume from there with position announcement.

### Phase 3: Play Handler Resume Awareness
For each play handler, after resolving the target item but BEFORE building the response:

**Books (PlayBookIntentHandler)**:
- Query Jellyfin for progress on the audiobook's children (tracks/chapters)
- Find the first track with `PlaybackPositionTicks > 0` or the first unplayed track
- Build the queue starting from that track, with offset for that track's position
- Auto-resume (no prompt — users expect books to continue where they left off)

**Video (PlayVideoIntentHandler)**:
- Query Jellyfin for progress on the movie/episode
- If `PlaybackPositionTicks > 0`, use the saved offset in the VideoApp directive
- Auto-resume (same as books)

**Music (PlayArtistSongsIntentHandler, PlayAlbumIntentHandler, PlayPlaylistIntentHandler)**:
- Query Jellyfin for progress on the artist/album/playlist's items
- If the user has a partially-listened queue, resume from the last played track (position 0 for each track — songs are short)
- Auto-resume the queue position but not within-song position

### Phase 4: Enhanced StartOverIntent
**Current**: Only restarts the currently-playing item from position 0.
**Enhanced**: 
1. Still restarts currently-playing item if one is active
2. If nothing is playing, finds the last-played item with progress and restarts that from position 0
3. **Clears Jellyfin `PlaybackPositionTicks`** for the item before playing (so next play starts fresh)
4. Requires `IUserDataManager` injection to clear progress

**New dependencies**: `ILibraryManager`, `IUserManager`, `IUserDataManager`

### Phase 5: Locale Strings + Interaction Models
New response strings needed (all 17 locales):
- `ResumingBook` — "Resuming {book_name} from chapter {chapter}."
- `ResumingVideo` — "Resuming {title} from {position}."
- `RestartingContent` — "Starting {title} from the beginning."
- `ResumingQueue` — "Resuming from {track_name}."

Interaction model: `AMAZON.StartOverIntent` is a built-in intent, so no model changes needed for restart. If we add a custom `RestartContentIntent` with slots (e.g., "restart book {name}"), that would need model updates — but `AMAZON.StartOverIntent` should suffice for V1.

### Phase 6: Unit Tests
- `ResumeIntentHandlerTests` — test Jellyfin fallback tier
- `PlayBookIntentHandlerTests` — test chapter resume from server-side progress
- `StartOverIntentHandlerTests` — test non-playing item restart + progress clearing
- Each test mocks `IUserDataManager.GetUserData()` to return controlled progress data
<!-- SECTION:PLAN:END -->

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
