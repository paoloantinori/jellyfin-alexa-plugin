# Draft: Jellyfin forum post for Plex Alexa skill migrants

Status: DRAFT, not posted. Review before publishing. Target: forum.jellyfin.org
(introduce yourself section or a new thread when appropriate).

---

## Title: Alexa skill for Jellyfin: a home for former Plex Alexa users

Plex disabled its official Alexa skill on June 15, 2026 ([announcement](
https://forums.plex.tv/t/important-update-regarding-the-plex-alexa-skill/938054/1)).
If you used it to stream your library to Echo devices and are looking for a
replacement, [jellyfin-alexa-plugin](https://github.com/paoloantinori/jellyfin-alexa-plugin)
is a native Jellyfin plugin (no separate server or container to run) that creates
a personal Alexa skill for each Jellyfin user.

**What it does:**

- Play songs, albums, artists, playlists, podcasts, and audiobooks by voice
- Play movies, TV episodes, and live TV channels on Echo Show
- Browse your library on Echo Show with tappable APL cards
- Multi-chapter audiobooks get a full-book seek bar via HLS streaming, with
  resume-from-position across sessions
- Multi-turn song search ("find a song" -> guided by artist and keywords)
- Per-user library access controls, fuzzy matching, and phonetic search that
  handles accented speech (e.g. Italian speakers saying English artist names)
- 17 locales across 11 languages (English, Spanish, French, German, Italian,
  Portuguese, Arabic, Dutch, Hindi, Japanese)
- Free and open source (GPL-3.0), installable from the Jellyfin plugin catalog

**What it honestly cannot do** (Amazon platform constraints, not plugin bugs;
every custom Alexa skill shares these):

- No native scrubber during music playback (reserved for Amazon's Music Skill
  API partners). Audiobooks on Echo Show DO get a seek bar via a video wrapper.
- "Stop", "next", and "previous" during playback are often claimed by your
  default music service. "Pause" always works.
- A new "play X" request during playback goes to your default music service;
  you need to include the skill's invocation name.

**Getting started:**

1. Install the plugin from the Jellyfin catalog (search for "Alexa Skill")
2. Follow the [setup guide](https://github.com/paoloantinori/jellyfin-alexa-plugin#installation)
   to create a free Amazon developer account and link your Jellyfin user
3. The skill takes about 10 minutes to set up; no AWS account needed, just an
   Amazon developer account (free)

The project is in active development. Feedback, bug reports, and contributions
are welcome on [GitHub](https://github.com/paoloantinori/jellyfin-alexa-plugin).
