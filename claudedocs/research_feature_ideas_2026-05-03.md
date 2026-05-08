# Feature Research Report: Jellyfin Alexa Plugin

**Date**: 2026-05-03
**Depth**: Exhaustive (4 parallel research agents)
**Sources**: Kodi/Kanzi, Audiobookshelf, Navidrome, Emby, Plex, Spotify, Home Assistant/Music Assistant, official ASK SDK docs, 20+ Jellyfin community plugins

## Already Implemented (Current Plugin)

- Basic transport: Play/Pause/Resume/Stop/Next/Previous/StartOver
- Loop/Shuffle controls (song loop, shuffle on/off)
- Play by: song, album, artist, playlist, favorites, last added, channel, video
- Mark/unmark favorite
- Media info ("what is playing") with progress reporting
- Disambiguation with Yes/No multi-turn flow
- Progressive responses for long operations
- 12 locales (en-US/AU/CA/GB/IN, es-ES/MX/US, de-DE, fr-FR/CA, it-IT)
- All 5 AudioPlayer event handlers (Started/Stopped/Finished/Failed/NearlyFinished)
- Error handling with localized messages, 6s timeout, unique error references
- Request signature verification
- Account linking via LWA

---

## Candidate Features (for Backlog Creation)

### Category A: New Voice Interactions & Intents

| # | Feature | Inspired By | Description | Impact |
|---|---------|-------------|-------------|--------|
| A1 | Play random content | Kodi, Navidrome, Spotify | "Play a random movie/album/song" or "Play a random song from {genre}" | High |
| A2 | Play by genre | Kodi, Navidrome | "Play some jazz/rock/electronic music" | High |
| A3 | Continue watching/listening | Kodi, Audiobookshelf | "Continue watching" / "Continue listening" resumes last in-progress media | High |
| A4 | Time-based seek | Kodi, Audiobookshelf | "Skip forward 2 minutes" / "Go back 30 seconds" | High |
| A5 | Chapter navigation | Audiobookshelf | "Next chapter" / "Previous chapter" for audiobooks/podcasts | High |
| A6 | In-progress list | Audiobookshelf | "What am I listening to?" / "What was I watching?" lists recent in-progress items | Medium |
| A7 | Play specific episode | Kodi | "Play season 4 episode 10 of The Office" | Medium |
| A8 | Library browsing | Kodi | "What albums do you have by {artist}?" / "Show me {genre} movies" | Medium |
| A9 | Recommendations | Kodi, Spotify | "Recommend something to watch" / "Play something I'd like" | Medium |
| A10 | Sleep timer | Audiobookshelf | "Stop playing in 30 minutes" using token-encoded deadline (no external scheduler) | Medium |
| A11 | Play mood/contextual music | Spotify, AudioMuse plugin | "Play something relaxing" / "Play upbeat music" using genre/mood tags | Medium |
| A12 | Party mode (shuffle all) | Kodi | "Party mode" shuffles entire music library | Low |
| A13 | Rate currently playing | Navidrome | "Rate this 4 stars" / "Give this 5 stars" | Low |
| A14 | Play trailer | Kodi | "Play the trailer for {movie}" | Low |

### Category B: Resilience, Fault Tolerance & Error Handling

| # | Feature | Inspired By | Description | Impact |
|---|---------|-------------|-------------|--------|
| B1 | Fuzzy string matching for search | infinityofspace/jellyfin_alexa_skill (RapidFuzz) | Tolerate typos in artist/album/song names ("play beetles" matches "Beatles") | High |
| B2 | Graceful unsupported intent handling | Official ASK audio sample | Return "not supported yet" for built-in intents the skill doesn't fully support (certification requirement) | High |
| B3 | Retry logic with exponential backoff | General best practice | Retry failed Jellyfin API calls with backoff before reporting error to user | Medium |
| B4 | Interceptor-based persistence middleware | Official ASK sample (DynamoDB pattern) | Auto load/save playback state in request/response pipeline instead of per-handler | Medium |
| B5 | Connection health check in config | Meilisearch plugin pattern | "Test Connection" button on config page to validate server URL, SSL, and credentials | Medium |
| B6 | Cached fallback for search failures | Gelato plugin decorator pattern | Cache last successful search results; return cached data if Jellyfin API is temporarily down | Low |
| B7 | Structured diagnostics dashboard | Meilisearch REST status endpoints | Expose request counters per intent, avg response time, error rate via REST API for admin monitoring | Low |

### Category C: UX & Configuration Improvements

| # | Feature | Inspired By | Description | Impact |
|---|---------|-------------|-------------|--------|
| C1 | Album art on Echo Show/Fire TV | Official ASK SDK metadata | Pass cover art URLs from Jellyfin to AudioPlayer metadata for visual display on screen devices | High |
| C2 | Enhanced config page with diagnostics | SSO plugin, Streamyfin, Meilisearch | Add connection test, skill status indicator, OAuth token status, intent usage stats to config page | Medium |
| C3 | Dialog delegation for complex intents | Official ASK SDK | Use addDelegateDirective for multi-slot intents ("play {song} by {artist} from {album}") | Medium |
| C4 | SSML for natural speech output | infinityofspace skill | Add `<break/>` between title and artist, prosody for emphasis, natural pauses | Medium |
| C5 | APL visual templates | Emby AlexaController | Custom layouts for Now Playing, Queue, Search Results on Echo Show devices | Medium |
| C6 | Proactive event notifications | ASK Proactive Events API | "New episodes of {show} are now available" pushed to users via Alexa notifications | Low |
| C7 | PlaybackController interface support | Official ASK sample | Ensure hardware button presses (play/pause/next/previous on device) are properly handled | Medium |

### Category D: Advanced Capabilities

| # | Feature | Inspired By | Description | Impact |
|---|---------|-------------|-------------|--------|
| D1 | Queue management (enqueue, play next) | Music Assistant, Spotify | "Add this to my queue" / "Play this next" via PlaybackNearlyFinished chaining | High |
| D2 | Radio mode (auto-similar tracks) | Navidrome getSimilarSongs, Music Assistant | After current track/album ends, auto-generate similar tracks to continue playing | High |
| D3 | Voice-based user identification | Emby AlexaController | "Learn my voice" maps Alexa voice ID to Jellyfin user account for per-user libraries | Medium |
| D4 | Parental controls via voice | Emby AlexaController | Enforce Jellyfin parental ratings based on voice-identified user profile | Medium |
| D5 | Dynamic playlist generation | SmartLists, Playlist Generator, AudioMuse | Generate playlists based on mood, genre, recently added, or listening history on demand | Medium |
| D6 | Last.fm/ListenBrainz scrobbling | Last.fm plugin, ListenBrainz plugin | Track playback via Alexa and report to music scrobbling services | Low |
| D7 | External playlist import | jellyplist plugin | Import playlists from Spotify or other services into Jellyfin via voice command | Low |
| D8 | Multi-device playback control | Music Assistant | "Play this on the living room speaker" / transfer queue between devices | Low |

### Category E: Plugin Infrastructure & Bugs

| # | Feature | Inspired By | Description | Impact |
|---|---------|-------------|-------------|--------|
| E1 | Fix plugin icon not displaying | Current bug (v0.2.0.4) | Plugin icon/image not showing in Jellyfin plugin list page | High |
| E2 | Fix progressive response HttpClient reuse | Backlog task jf-25 | HttpClient not properly reused for progressive responses | High |
| E3 | Manifest versioning (targetAbi) | Streamyfin plugin pattern | Proper targetAbi field for Jellyfin version compatibility in manifest.json | Medium |
| E4 | Service registration cleanup | Gelato IPluginServiceRegistrator | Clean DI registration using IPluginServiceRegistrator pattern | Low |

---

## Sources

- **Kodi/Kanzi** (github.com/m0ngr31/kanzi) - 120+ intents, most comprehensive open-source media voice control
- **Audiobookshelf** (github.com/jonas-dev/audiobookshelf-alexa, github.com/sevenlayercookie/abs-alexa) - Sleep timer, chapter nav, progress sync
- **Navidrome/AskNavidrome** (github.com/rosskouk/asknavidrome) - Genre play, radio mode, star/rate
- **Emby AlexaController** (github.com/jthornca/Emby.AlexaController) - Voice ID, parental controls, APL
- **infinityofspace/jellyfin_alexa_skill** (99 stars, Python) - Fuzzy matching, competing Jellyfin skill
- **Official ASK SDK docs** (Context7) - Dialog delegation, interceptors, progressive responses, persistence
- **Official ASK audio sample** (github.com/alexa-samples/skill-sample-nodejs-audio-player, 474 stars) - Gold standard AudioPlayer patterns
- **Home Assistant/Music Assistant** (Context7 docs) - Radio mode, queue management, multi-room
- **Jellyfin plugins**: Gelato, Last.fm, Meilisearch, SSO, Favorited Songs Playlist, SmartLists, AudioMuse-AI
- **awesome-jellyfin/awesome-jellyfin** (7,649 stars) - Comprehensive plugin listing
