# Research Report: Music Assistant Feature Inspiration for Jellyfin Alexa Plugin

**Date**: 2026-05-03
**Depth**: Exhaustive
**Confidence**: 0.85 (high — based on official docs, GitHub repos, community discussions)

---

## Executive Summary

Music Assistant (MA) is a mature, open-source music library manager by the Open Home Foundation, tightly integrated with Home Assistant. Its voice control capabilities are implemented through community blueprints, companion integrations (notably SpotifyPlus), and evolving HA Assist intents. MA's feature set spans far beyond simple playback — covering queue management, party modes, podcasts, audiobooks, lyrics, DSP, multi-room sync, and more.

This report maps MA features to actionable inspiration for the Jellyfin Alexa plugin, categorized by implementation feasibility.

---

## 1. Music Assistant Core Feature Inventory

### 1.1 Playback & Queue Management (MA Core)

| Feature | MA Implementation | Jellyfin Plugin Status |
|---------|-------------------|----------------------|
| Queue management (enqueue, play next, clear) | Per-player queue with native enqueue or flow mode | **Planned** (jf-58) |
| Radio mode / auto-similar tracks ("Don't Stop The Music") | When queue ends, auto-plays similar tracks from sources | **Planned** (jf-59) |
| Gapless playback | Native enqueue or flow-mode stitching | N/A (Alexa AudioPlayer limitation) |
| Crossfade / smart fading | DSP pipeline per player | N/A (Alexa limitation) |
| Volume normalization | Stream server settings, per-player adjustment | Not applicable |
| Shuffle/repeat | Queue-level controls | Partially implemented |
| Transfer playback between players | Move queue between players | Not applicable (single Alexa device) |
| Playback sync (multi-room) | Sendspin protocol | Not applicable |

### 1.2 Media Discovery & Browsing

| Feature | MA Implementation | Jellyfin Plugin Status |
|---------|-------------------|----------------------|
| Unified search across all sources | Single search hits all connected providers | **Partial** — individual search intents |
| Play by genre | Genre browsing and radio | **Implemented** (jf-36) |
| Play by mood | Mood/contextual music | **Implemented** (jf-44) |
| Play random content | Random from library | **Implemented** (jf-35) |
| Recommendations | Dynamic suggestions based on listening | **Implemented** (jf-42) |
| In-progress media | "Continue listening" section | **Implemented** (jf-39) |
| Library browsing | Browse by category | **Implemented** (jf-41) |
| Fuzzy string matching | Track linking across sources | **Planned** (jf-45) |
| Artist info / metadata | Extended artist info from providers | **Partial** (MediaInfoIntent) |

### 1.3 Podcasts, Audiobooks & Radio (MA v2.4+)

| Feature | MA Implementation | Jellyfin Plugin Status |
|---------|-------------------|----------------------|
| Podcast support | RSS, Subsonic, YouTube, Podcast Index, BBC Sounds, iTunes | **Not present** |
| Audiobook support | Audible, Audiobookshelf, local files; chapter tracking | **Partial** (GoToChapterIntent jf-38) |
| Progress tracking | Resume where left off, chapter markers | **Partial** (InProgressMediaListIntent) |
| Radio stations | Multiple radio providers (Radio Paradise, internet radio) | **Partial** (PlayChannelIntent) |

### 1.4 Lyrics & Visual Features

| Feature | MA Implementation | Jellyfin Plugin Status |
|---------|-------------------|----------------------|
| Lyrics display | Time-synced lyrics (LRCLIB, Genius, local LRC) | **Not present** |
| Karaoke mode | Party plugin with highlighted lyrics | **Not present** |
| Album art on devices | Cover art metadata in streams | **Planned** (jf-52) |
| APL visual templates | N/A (MA uses Cast/AirPlay) | **Planned** (jf-56) |

### 1.5 Party / Social Features (MA v2.8)

| Feature | MA Implementation | Jellyfin Plugin Status |
|---------|-------------------|----------------------|
| Party mode / Jukebox | QR code scanning, guest queue management | **Not present** — interesting for Alexa |
| Song boosting | Guests use credits to prioritize tracks | **Not present** |
| Rate limiting | Token bucket for guest actions | **Not applicable** (Alexa has no multi-user) |

### 1.6 Voice Control Architecture (MA + Community)

MA itself does NOT have built-in voice intents. Voice control is community-driven via:

1. **Local Assist Blueprint** — Custom sentences for HA Assist, strictly-formulated requests
2. **LLM-Enhanced Blueprint** — Uses OpenAI/Gemini for flexible natural language → music playback
3. **SpotifyPlus Integration** — 10+ dedicated voice intents for Spotify control
4. **Custom Sentences** — HA conversation triggers for transfer, play, search

The SpotifyPlus integration is the richest voice implementation with these intent categories:
- **Deck Control**: pause, stop, resume, start, next, previous, restart
- **Search & Play**: play song, album, artist, playlist, podcast, radio
- **Favorites**: play favorite, add/remove favorite, like current song
- **Now Playing Info**: what's playing, artist info, album info
- **Volume Control**: set volume, increase, decrease, mute/unmute, set step
- **Repeat/Shuffle**: repeat all, one, off; shuffle on/off
- **Transfer**: transfer playback to [room/device]
- **Podcast**: play podcast, play podcast episode

---

## 2. Feature Inspiration — Ranked by Impact & Feasibility

### Tier 1: High Impact, Feasible (Aligns with Alexa Skill Model)

These features map well to Alexa's intent model and would significantly enhance the Jellyfin plugin.

#### 2.1 Podcast Support — Browse & Play Podcasts
- **Inspiration**: MA supports podcasts from RSS, YouTube, Podcast Index
- **Alexa Implementation**: `PlayPodcastIntent` — "Play the podcast [name]", "Play the latest episode of [podcast]"
- **Jellyfin API**: Jellyfin has podcast support via libraries
- **Impact**: HIGH — podcasts are a major use case for voice
- **Feasibility**: HIGH — Jellyfin API supports this, Alexa slot model is straightforward

#### 2.2 "What's Playing" Enhanced — Rich Now-Playing Info
- **Inspiration**: SpotifyPlus has dedicated "now playing info" intents (artist info, album info, track details)
- **Alexa Implementation**: Enhance `MediaInfoIntent` with follow-ups:
  - "What album is this from?"
  - "Tell me about this artist"
  - "When was this released?"
  - "What genre is this?"
- **Impact**: MEDIUM-HIGH — improves the conversational feel
- **Feasibility**: HIGH — metadata available from Jellyfin API

#### 2.3 Like/Thumbs Up Current Song
- **Inspiration**: SpotifyPlus "Like Current Song" intent; MA "favorite current song" button
- **Alexa Implementation**: "I like this song", "Thumbs up", "Add this to my favorites"
- **Already exists**: `MarkFavoriteIntent` — but could be enhanced with "like this" natural language
- **Impact**: MEDIUM — good UX improvement
- **Feasibility**: TRIVIAL — already implemented, just needs more utterances

#### 2.4 Play Recently Added
- **Already implemented**: `PlayLastAddedIntent`
- **Enhancement**: "Play something new" — play recently added with time context ("added this week")

#### 2.5 Play by Decade / Era
- **Inspiration**: MA browsing by year/decade
- **Alexa Implementation**: "Play music from the 80s", "Play 90s rock"
- **Impact**: MEDIUM — fun browsing capability
- **Feasibility**: HIGH — Jellyfin API supports year-based queries

#### 2.6 Add to Queue / Play Next
- **Inspiration**: MA's full queue management (add, move, clear, play next)
- **Alexa Implementation**: "Play this next", "Add this to my queue", "Clear my queue"
- **Planned**: jf-58 (Queue management)
- **Impact**: HIGH — fundamental playback control
- **Feasibility**: MEDIUM — Alexa AudioPlayer queue manipulation has constraints

#### 2.7 Themed / Contextual Playlists
- **Inspiration**: MA mood music; SpotifyPlus party/workout/relax modes
- **Alexa Implementation**: "Play workout music", "Play dinner music", "Play focus music"
- **Already partially implemented**: `PlayMoodMusicIntent`
- **Enhancement**: More mood categories, time-of-day awareness ("Play morning music")

### Tier 2: Medium Impact, Moderate Feasibility

#### 2.8 Audiobook Progress & Chapter Navigation
- **Inspiration**: MA shows full book as single progress bar with chapter dots
- **Alexa Implementation**: "How far am I in this book?", "Skip to chapter 5", "What chapter am I on?"
- **Partially implemented**: `GoToChapterIntent` (jf-38)
- **Enhancement**: Progress percentage, remaining time, chapter listing

#### 2.9 Song Lyrics / "Sing Along" Mode
- **Inspiration**: MA lyrics with time-synced highlighting; karaoke mode
- **Alexa Implementation**: "What are the lyrics to this song?", "Show me the lyrics" (for Echo Show)
- **Impact**: MEDIUM — mostly visual device feature
- **Feasibility**: LOW-MEDIUM — requires lyrics API integration, APL for display

#### 2.10 Radio Station Presets
- **Inspiration**: MA radio providers (Radio Paradise, internet radio)
- **Alexa Implementation**: "Play my radio station", "Play Radio Paradise", "Tune to jazz radio"
- **Already partially implemented**: `PlayChannelIntent`
- **Enhancement**: Named presets, genre-based radio browsing

#### 2.11 Playback Speed Control (for audiobooks/podcasts)
- **Inspiration**: MA audiobook playback controls
- **Alexa Implementation**: "Play faster", "Slow down", "Set speed to 1.5x"
- **Impact**: MEDIUM — important for spoken content
- **Feasibility**: LOW — Alexa AudioPlayer doesn't natively support speed changes

#### 2.12 Listen History / "Play It Again"
- **Inspiration**: MA listening history with LastFM/ListeBrainz scrobbling
- **Alexa Implementation**: "What did I listen to yesterday?", "Play that song again", "Play my most played"
- **Impact**: MEDIUM — personalization
- **Feasibility**: MEDIUM — requires tracking state in Jellyfin

### Tier 3: Interesting Concepts, Lower Feasibility

#### 2.13 Transfer Playback Between Devices
- **Inspiration**: MA's signature feature — move queue between players
- **Alexa Implementation**: "Move this to the living room", "Continue on my phone"
- **Impact**: HIGH but...
- **Feasibility**: VERY LOW — Alexa skills can't control playback on other Alexa devices directly

#### 2.14 Multi-Room / Group Playback
- **Inspiration**: MA Sendspin multi-brand sync
- **Not feasible** with Alexa skill model — Alexa handles multi-room natively

#### 2.15 Party / Guest Mode
- **Inspiration**: MA Party Plugin with QR codes, guest queue, song boosting
- **Not directly applicable** — Alexa is single-user per session, but could inspire a "request a song" party mode

---

## 3. SpotifyPlus Companion Skills — Deep Feature Analysis

The SpotifyPlus HA integration provides the richest voice control reference. Key patterns:

### 3.1 Intent Categories Worth Emulating

**Deck Control** (already mostly covered by Amazon built-in intents):
- Pause, resume, stop, next, previous, restart

**Search & Play** (Jellyfin plugin already has many of these):
- Play song, album, artist, playlist, podcast, radio
- Enhancement: Play by decade, play by label, play compilation

**Favorites Management**:
- "Add this to my favorites" (already have MarkFavorite)
- "Remove from favorites" (already have UnmarkFavorite)
- "Like this song" (natural language for favorite)
- "Play my favorites" (already have PlayFavorites)

**Now Playing Info** (BIGGEST GAP in Jellyfin plugin):
- "What song is this?" → title + artist
- "What album is this from?" → album name
- "Who sings this?" → artist name
- "What year was this released?" → year
- "Tell me about this artist" → biography snippet
- "How long is this song?" → duration

**Volume Control** (handled by Alexa natively, but...):
- Alexa handles volume natively, no need to implement

### 3.2 SpotifyPlus Natural Language Patterns Worth Adopting

German examples from community translations show useful patterns:
- "Ich mag [dieses] Lied" → Like current song
- "Spiele [das] Radio von {artist}" → Artist radio
- "Spiele [den] Song {song}" → Play specific song
- "Starte [die] Playlist {playlist}" → Play playlist

---

## 4. Summary: Top 10 Feature Recommendations

| Priority | Feature | MA Inspiration | Effort | Impact |
|----------|---------|---------------|--------|--------|
| 1 | **Enhanced Now-Playing Info** | SpotifyPlus info intents | Low | High |
| 2 | **Podcast Browse & Play** | MA podcast providers | Medium | High |
| 3 | **Play by Decade/Era** | MA library browsing | Low | Medium |
| 4 | **Add to Queue / Play Next** | MA queue management | Medium | High |
| 5 | **Audiobook Progress Reporting** | MA audiobook tracking | Low | Medium |
| 6 | **Radio Mode / Don't Stop The Music** | MA DSTM feature | Medium | High |
| 7 | **More "Like This" Natural Language** | SpotifyPlus utterance patterns | Trivial | Medium |
| 8 | **Lyrics Display (Echo Show)** | MA lyrics + karaoke | Medium | Medium |
| 9 | **Time-of-Day Awareness** | MA contextual suggestions | Low | Medium |
| 10 | **Play Count / History Queries** | MA listening history | Medium | Medium |

---

## 5. Sources

1. Music Assistant Official Site: https://www.music-assistant.io/
2. MA Voice Control Docs: https://www.music-assistant.io/integration/voice/
3. MA Voice Support Repository: https://github.com/music-assistant/voice-support
4. MA Server Repository: https://github.com/music-assistant/server
5. MA v2.4 Blog ("Next Big Hit"): https://www.music-assistant.io/blog/2025/03/05/music-assistants-next-big-hit/
6. MA v2.7 Blog ("Taking Over The Airwaves"): https://www.music-assistant.io/blog/2025/12/17/music-assistant-2-7/
7. MA v2.8 Blog ("Let's Get This Party Started"): https://www.music-assistant.io/blog/2026/03/25/music-assistant-2-8/
8. SpotifyPlus Voice Assist Discussion: https://github.com/thlucas1/homeassistantcomponent_spotifyplus/discussions/205
9. SpotifyPlus Voice Music Control Thread: https://community.home-assistant.io/t/voice-music-control-with-spotifyplus-and-ha-voice-pe/837357
10. MA Party Plugin: https://www.music-assistant.io/plugins/party/
11. HA Integration Feature Request Discussion: https://github.com/orgs/home-assistant/discussions/1751
12. MA Queue Actions: https://github.com/droans/mass_queue
