# Research: Visual Support for Echo Show Devices

**Date**: 2026-05-10
**Trigger**: User asked "quali canzoni abbiamo dei Soul Coughing" on Echo Show — Alexa spoke 5 results but displayed nothing on screen.

## Executive Summary

The Jellyfin Alexa Skill already has APL infrastructure for the **Now Playing** screen (album art, title, controls) and **Queue** display. However, all list/search/browse responses are **speech-only**. Five handlers need APL list templates to provide visual output on Echo Show devices.

## Current State

### What Works (APL Already Implemented)
- **Now Playing screen** (`AplHelper.BuildNowPlayingDirective`) — album art, title, artist, playback controls
- **Queue display** (`AplHelper.BuildQueueDirective`) — scrollable track list with tap-to-play
- **Device detection** (`AplHelper.DeviceSupportsApl()`) — checks `Alexa.Presentation.APL` in supportedInterfaces

### What's Missing (Speech-Only, No Visual)
| Handler | Response Type | Visual Needed |
|---------|--------------|---------------|
| `BrowseLibraryIntentHandler` | Numbered list of artists/albums/songs | Scrollable list with thumbnails |
| `QueryArtistLibraryIntentHandler` | Songs/albums by artist | List with album art |
| `SearchMediaIntentHandler` | Disambiguation of search results | Search result cards |
| `ListQueueIntentHandler` | Queue contents | Queue list (APL already exists but unused here) |
| `InProgressMediaListIntentHandler` | In-progress media with timestamps | Progress list with thumbnails |
| `MediaInfoIntentHandler` | Info about current track | Detail card |

## Technical Findings

### APL vs Display.RenderTemplate
- **Display.RenderTemplate is deprecated** since August 2021 (marked `[Obsolete]` in Alexa.NET)
- **APL is the current standard** — fully custom JSON layouts with responsive design
- The project correctly uses APL, not Display templates

### APL Document Architecture
An APL visual response is a `RenderDocument` directive added alongside speech in the same response:
```
Response = { outputSpeech, directives: [APL RenderDocument] }
```
The directive contains:
- `document` — JSON layout definition (components, styles, resources)
- `datasources` — Data to bind into the template
- `token` — Identifier for subsequent ExecuteCommands

### Alexa.NET.APL NuGet Package
The project currently does NOT reference `Alexa.NET.APL` (8.4.0). Instead, it uses a custom `AplRenderDocumentDirective` class in the `Alexa/Directive/` namespace. This works but misses strongly-typed APL component builders and responsive templates (`AlexaDetail`, `AlexaHeadline`, `AlexaPaginatedList`, `AlexaTransportControls`).

### Recommended APL Templates for List Responses
1. **`AlexaPaginatedList`** (from `alexa-layouts` import) — Horizontal pages of items, touch-scrollable
2. **`AlexaTextList`** — Simple vertical text list with optional images
3. **`AlexaDetail`** — Single item detail view (for MediaInfo)
4. **Custom Container + Sequence** — Current queue template pattern (already working)

### AudioPlayer Metadata
All `AudioPlayerPlayDirective` responses should include `AudioItemMetadata` (title, subtitle, art, backgroundImage). This renders the **native Now Playing screen** without needing APL. The current `BuildAudioPlayerResponse` already does this.

## Implementation Approach

### Phase 1: APL List Template for Browse/Search (High Impact)
Create a reusable APL list template in `AplHelper` (similar to existing `QueueDocument`) that shows:
- Item thumbnail/art on the left
- Title + subtitle on the right
- Tap-to-select touch wrappers
- Dark theme, responsive sizing

Then attach it to the 5 list-returning handlers.

### Phase 2: Add Alexa.NET.APL Package (Optional, Low Priority)
Migrate from custom `AplRenderDocumentDirective` to the official `Alexa.NET.APL` package for:
- Strongly-typed component builders
- Responsive template components
- `APLSkillRequest` with viewport data
- `APLSupport.Add()` registration

### Key Design Decisions
1. **Reuse vs. per-handler templates**: A single `ListTemplate` in AplHelper can serve all 5 handlers with different data. The queue template is a good pattern to follow.
2. **Touch interaction**: Each list item should fire a `SendEvent` so users can tap to select on Echo Show. This requires handling `Alexa.Presentation.APL.UserEvent` requests (already supported via `GetTouchEventArgument()`).
3. **Fallback**: Always include full speech output so non-screen devices work identically. APL is additive, never replaces speech.

## Sources
- Alexa.NET source code: timheuer/alexa-skills-dotnet (GitHub)
- Alexa.NET.APL package: stoiveyp/Alexa.NET.APL (NuGet 8.4.0)
- Alexa APL documentation: developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/
- Project codebase: AplHelper.cs, BaseHandler.cs, Intent handlers
