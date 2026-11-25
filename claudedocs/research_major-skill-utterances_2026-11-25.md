# Major-skill utterance mining for JF-963 (alignment delta)

Research date: 2026-11-25. Phase: RESEARCH ONLY; no template touched. Every sample below is verbatim from the cited artifact unless marked otherwise. Fetched copies of all GitHub sources retained under `/tmp/jf963/models/`.

## 0. Method and sources

| # | Source | Kind | Reachable | Where |
|---|--------|------|-----------|-------|
| 1 | SMAPI via ask CLI 2.30.7 | API | no third-party search exists | section 1 |
| 2 | Alexa Skills Store / retail dp pages | web | bot-walled live; recovered via Wayback snapshots | section 2 |
| 3 | Amazon official docs (Music Skill API, Standard Built-in Intents, Video Skill API) | web | yes | section 3 |
| 4 | Plex official Alexa skill docs (support.plex.tv, via Wayback) | web | yes (archived) | section 4 |
| 5 | Music Assistant (official docs + server provider source + prototype) | web + GitHub | yes | section 5 |
| 6 | Emby official Alexa doc (emby.media) | web | yes | section 6 |
| 7 | My Media for Alexa official command list (mymediaalexa.com, 5 locales) | web | yes | section 7 |
| 8 | GitHub open-source media-skill models | gh api / raw fetch | yes | section 8 |

Sections 2 through 8 each carry verbatim examples with their citation. Section 9 is the consolidated corpus, section 10 the delta against our models, section 11 the recommended alignment list.

## 1. SMAPI store search (source class: API)

**Finding: SMAPI offers no third-party skill search.** `ask smapi --help` enumerates the subcommands; the only skill-listing command is `list-skills-for-vendor` (own vendor). There is no `search-skills`, no public `get-skill-details`, no catalog search for other vendors' skills. Third-party skill discovery happens only through the consumer-facing Skills Store.

## 2. Alexa Skills Store example phrases (source class: store listings)

Live access is blocked: `skills-store.amazon.com` is a redirect shell (its deeplink page contains only `var fragment = '' + '/dp/B01NBB1INY'` and a JS redirect to alexa.amazon.com; no `examplePhrases` anywhere in the HTML), and live `amazon.com/dp/...` retail pages return Amazon's 3.7 KB automation-access bot-wall to curl and to crw scrape. Working route: Wayback Machine snapshots of the retail pages. (All fetched 2026-11-25.)

**Plex skill** (ASIN B01NBB1INY), snapshot `https://web.archive.org/web/20260309050753id_/https://www.amazon.com/Plex-Inc/dp/B01NBB1INY`:

- Description-prose phrases, verbatim: "Alexa, ask Plex to play Time Travel radio" / "What's On Deck," / "Suggest Something to Watch" / "Alexa, ask Plex to play some music"
- Invocation Name: "plex". Supported Languages: English (AU/CA/GB/IN/US), German (DE).
- No example-phrase carousel in this snapshot; the phrases above come from description prose.
- Description pointer, verbatim: "To find out what all you can do with the Plex Skill and see a list of supported Plex players and detailed voice commands, check out: https://support.plex.tv/articles/categories/plex-apps/alexa-voice-control/"

**iHeartRadio** (ASIN B0842B8V2Z), snapshot `https://web.archive.org/web/20211201080546id_/https://www.amazon.com/iHeartRadio/dp/B0842B8V2Z`. The store's example-phrase carousel, verbatim, en-US:

- "Alexa, play z. one hundred"
- "Alexa, play North Pole Radio on iHeartRadio"
- "Alexa, make a song request"

Invocation Name: "iheart radio". Store description verbatim excerpt: "And be sure to make iHeartRadio your default music, podcast, and artist/genre service so you don't have to say \"on iHeartRadio\" at every request."

**Documented negatives** (searched, not found, not invented): TuneIn (no retail skill detail page; first-party built-in radio source), Amazon Music (first-party service, no store page), My Media for Alexa (no retail dp page; its vendor command doc IS public, section 7), Kodi/Jellyfin media skills (no store detail pages surfaced). A Home Assistant store listing (B0772J1QKB) surfaced in search but the page itself is bot-walled; its example phrases could not be quoted.

## 3. Amazon official documentation (source class: platform docs)

All pages HTTP 200 via curl, fetched 2026-11-25.

### 3.1 Music Skill API, "Understand Voice Modeling" (developer.amazon.com/en-US/docs/alexa/music-skills/understand-voice-modeling.html, page footer "Last updated: Nov 27, 2023")

The platform's OWN documented request families for music skills. Verbatim, placeholders kept:

- "Alexa, play music on <skill name>." / "Alexa, play <skill name>."
- "Alexa, play music by <artist name> on <skill name>."
- Variations: "Alexa, play songs by <artist name> on <skill name>." / "Alexa, I'd like to hear music by <artist name> from <skill name>." / "Alexa, let's hear songs by <artist name> from <skill name>."
- Example: "Alexa, play music by Fleet Foxes on <skill name>."
- "Alexa, play the album <album name> on <skill name>."
- Variations: "Alexa, play album <album name> by <artist name> on <skill name>." / "Alexa, play the <album name> album on <skill name>." / "Alexa, I'd like to listen to the album <album name> by <artist name> on <skill name>."
- Example: "Alexa, play the album Vitalogy on <skill name>."
- "Alexa, play the song <track name> on <skill name>."
- Variations: "Alexa, play track <track name> by <artist name> on <skill name>." / "Alexa, I'd like to hear the song <track name> on <skill name>." / "Alexa, play song <track name> from <album name> on <skill name>."
- Example: "Alexa, play the song Poker Face on <skill name>."
- "Alexa, play the playlist <playlist name> on <skill name>."
- Variations: "Alexa, play the <playlist name> playlist on <skill name>." / "Alexa, play the <skill name> playlist <playlist name>." / "Alexa, play my <playlist name> playlist on <skill name>."
- Example: "Alexa, play the All Time Hits playlist on <skill name>."
- "Alexa, play the station <station name> on <skill name>." / Variations: "Alexa, play my <station name> station from <skill name>." / "Alexa, play <station name> radio on <skill name>." / Example: "Alexa, play the station News Today on <skill name>."
- "Alexa, play <genre name> music on <skill name>." / Example: "Alexa, play ballroom dancing music on <skill name>."
- Ambiguous-request example: "Alexa, play Fun on <skill name>."
- Device phrases: "... in the <device location>." / "... from my <device type>." / "... from the <device location> <device type>." / Example: "Alexa, play songs by Lady Gaga from <skill name> in the kitchen."
- Music alarms: "Alexa, set an alarm for <recurrence> at <time> with <content> on <skill name>."

The artist-play family in the platform's own docs is overwhelmingly the "media-noun + by + artist" order: "play music by <artist>", "play songs by <artist>", "let's hear songs by <artist>", "I'd like to hear music by <artist>". No "play <artist> songs" order appears anywhere in the page.

### 3.2 MSAPI API Reference Overview (same docs tree)

Verbatim: "if a user says \"Alexa, play 'Alive' from the album Ten on <skill name>,\" the skill receives a GetPlayableContent request". Confirms the song-from-album scoping shape at platform level.

### 3.3 MSAPI Testing Guide (same docs tree)

Verbatim test-utterance list (in page order; podcast/video-relevant subset): "Alexa, pause." / "Alexa, resume." / "Alexa, stop." / "Alexa, next." / "Alexa, previous." / "Alexa, loop." / "Alexa, repeat." / "Alexa, shuffle." / "Alexa, I like this song." / "Alexa, I don't like this song." / "Alexa, fast-forward / rewind <time>." / "Alexa, skip ahead / back <time>." / "Alexa, go forward / back <time>." / "Alexa, restart / start over." / "Alexa, play the latest episode of <programName>." / "Alexa, play the latest (episode)." / "Alexa, play episode <#>." / "Alexa, play episode <#> of <ProgramName>." / "Alexa, play season <#>." / "Alexa, play season <#>, episode <#> of <ProgramName>."

Note "Alexa, I like this song." / "I don't like this song." are official liking utterances; ours match.

### 3.4 Standard Built-in Intents reference (developer.amazon.com/en-US/docs/alexa/custom-skills/standard-built-in-intents.html)

This doc publishes short "Common Utterances" per locale. Verbatim extracts (locale labels follow the doc's table order: nl, en-US, fr, de, hi, it, ja, pt, es; high confidence but page-order-derived per the extraction caveat):

- AMAZON.PauseIntent: en-US "pause", "pause that"; it-IT "Metti in pausa", "Mettila in pausa"; fr "pause", "pause ça"; de "pause (bitte)", "unterbrechung", "pausieren"; es "pausa", "puedes pausar".
- AMAZON.ResumeIntent: en-US "resume", "continue", "keep going"; it-IT "Riprendi", "Continua", "Riprendilo"; fr "reprends", "continue"; de "mach weiter", "weiterlesen", "fortsetzen"; es "reanuda", "continúa", "por favor reanuda".
- AMAZON.NextIntent: en-US "next", "skip", "skip forward"; it-IT "Il prossimo", "Il successivo"; fr "suivant", "prochain", "saute", "passe"; de "weiter", "zum nächsten gehen"; es "siguiente", "próxima".
- AMAZON.PreviousIntent: en-US "go back", "skip back", "back up"; it-IT "Vai indietro", "Il precedente"; fr "précédent", "celui d'avant"; de "vorheriges", "spiele das letzte"; es "a la previa", "previa".
- AMAZON.LoopOnIntent: en-US "loop", "loop on", "keep playing this"; it-IT "La modalità loop", "Vorrei attivare la modalità loop"; fr "mets en boucle", "boucle"; de "starte loop", "geloopt abspielen"; es "repetición continua", "activa la repetición continua".
- AMAZON.LoopOffIntent: en-US "loop off"; it-IT "Spegni la modalità loop"; fr "arrête le mode boucle"; de "deaktiviere loop"; es "apaga repetición continua".
- AMAZON.ShuffleOnIntent: en-US "shuffle", "shuffle on", "shuffle the music", "shuffle mode"; it-IT "Mi puoi attivare la modalità casuale", "Mi puoi attivare la modalità shuffle"; fr "mélanger", "mets la lecture aléatoire", "lecture aléatoire"; de "aktiviere shuffle", "aktiviere die Zufallswiedergabe"; es "activa el modo aleatorio", "activa modo mezcla".
- AMAZON.ShuffleOffIntent: en-US "stop shuffling", "shuffle off", "turn off shuffle"; it-IT "Mi puoi disattivare la modalità casuale", "Puoi spegnere la modalità shuffle"; fr "arrête la lecture aléatoire"; de "deaktiviere shuffle", "deaktiviere die Zufallswiedergabe"; es "apaga modo aleatorio", "para de mezclar".
- AMAZON.StartOverIntent: en-US "start over", "restart", "start again"; it-IT "Comincia da capo", "Ricomincia", "Riinizia" (sic, the doc's own spelling); fr "recommence", "commence à nouveau"; de "(bitte) von vorne (bitte)", "neustart", "beginne erneut"; es "empieza de nuevo", "empieza otra vez".
- AMAZON.RepeatIntent: en-US "repeat", "say that again", "repeat that"; it-IT "Ripeti", "Ripeti il passo"; fr "répètes", "redis"; de "wiederhole (bitte)", "sag das noch mal (bitte)"; es "repite", "puedes repetir".
- AMAZON.CancelIntent: en-US "cancel", "never mind", "forget it"; it-IT "Annulla", "Non importa", "Lascia stare"; fr "annule"; de "abbrechen", "vergiss es"; es "cancela", "olvídalo".
- AMAZON.StopIntent: en-US "stop", "off", "shut up"; it-IT "Ferma", "Smettila", "Basta"; fr "arrête", "stop", "stoppe"; de "stopp", "aufhören"; es "para", "apagar", "callar".

Purpose-column verbatim highlights: PauseIntent "You must implement this intent if your skill streams audio using the AudioPlayer interface." StopIntent "Exits the skill. Your skill must implement this intent and shouldEndSession must be true or null in the response." Each transport intent carries the without-invocation-name clause we already rely on ("users can invoke this intent without using your invocation name if your skill is currently playing audio or was the most recent skill to play audio").

Relevance note: these Common Utterances are what Amazon's built-in NLU already routes with empty intent schemas; our empty built-ins converge with the media-skill family norm and need no samples. Our deliberate it-IT StopIntent custom samples ("ferma", JF-402) sit exactly on the official it-IT Common Utterances ("Ferma", "Smettila", "Basta"): convergent, keep.

### 3.5 Video Skill API (understand-the-video-skill-api.html + video-skill-testing-guide.html)

Verbatim: "The video interfaces use the pre-built voice interaction model. This model gives you a set of predefined utterances that users say to control your device." Testing-guide utterances, verbatim: "Alexa, find Transparent" / "Alexa, watch season 2, episode 4 of Mozart in the Jungle" / "Alexa, fast forward" / "Alexa, fast forward two minutes" / "Alexa, rewind 10 seconds" / "Alexa, next" / "Alexa, previous" / "Alexa, play the latest season of <programName>" / "Alexa, go to settings".

Note the official video shape "find <title>" (bare find) and "watch season 2, episode 4 of <show>" (season-first with comma) both exist at platform level.

## 4. Plex official Alexa skill documentation (source class: vendor docs)

The official "Alexa Voice Commands" article (support.plex.tv id 236324808) no longer serves from the live KB (the live category page lists no Alexa articles; the support_articles sitemap contains zero alexa URLs). Recovered via Wayback: `https://web.archive.org/web/20220824231308/https://support.plex.tv/articles/236324808-alexa-voice-commands/` (article footer "Last modified on: March 27, 2019"). ALL phrases below verbatim from that snapshot.

Invocation pattern: "say Alexa, Ask Plex... or Alexa, Open Plex". Interchangeable pairs (verbatim list): Ask Plex and Tell Plex / Play and Put on / Recommend and Suggest / Change and Switch / TV show and Show / Hear and Listen. Escape: "Just say Alexa, cancel".

Common commands: "Ask Plex what's On Deck?" / "Ask Plex to suggest something to watch" / "Tell Plex to play The Big Lebowski" / "Tell Plex to play music by Bob Marley" / "Tell Plex to pause" / "Tell Plex to stop".

Continue Watching: "What can I continue watching?" / "What's on continue watching?" / "What was I in the middle of?" / "What movie was I in the middle of?" / "What show was I in the middle of?"

Recently Added: "What's been recently added?" / "What was recently added?" / "Recently added" / "What's new?"

Suggestions: "Can you suggest something?" / "Can you recommend something?" / "What's good?" / "Give me a suggestion" / "Suggest something to watch" / "Recommend me something" / "Hook me up" / "What should I watch?" / "I don't know what to watch" / "Put on something to watch" / "Suggest a movie to watch" / "Put on a movie" / "I want to watch a movie"

Music Recommendations: "What should I listen to?" / "Suggest something to listen to" / "Suggest some music" / "Put on some music" / "Play some music" / "I want to hear some music" / "Give me something to listen to"

Media Playback: "Play The Dark Knight" / "Start Westworld" / "Let's watch The Grand Budapest Hotel" / "I would like to watch Back To The Future" / "Play Monsters Inc. on Roku" / "I want to hear music by Right Said Fred on Kitchen TV" / "Put on the album Dookie on iPhone"

TV playback: "Play the show Westworld" / "Put on the tv show Stranger Things" / "Let's watch the show Twin Peaks" / "Play season 2 of 30 Rock" / "Put on season 4 of The Wire" / "Play episode You Only Move Twice of The Simpsons" / "Put on episode Swiss Miss of Archer" (episodes addressed BY TITLE, not number)

Music playback:

- Library Radio: "Play some music" / "Play library radio" / "Play [library name] library radio"
- Time Travel Radio: "Play time travel radio" / "Play [library name] time travel radio"
- Artist Radio: "Play Lana Del Ray radio" / "Play [artist name] radio"
- By Artist: "Put on music by Bob Marley" / "Play music by Adele" / "Let's hear music by Elton John" / "Shuffle music by Pink Floyd" / "Shuffle Queen" / "Play the artist Rihanna" / "I want to hear songs by Avicii"
- By Album: "Shuffle the album Thriller" / "Put on the album Nevermind by Nirvana" / "I want to hear the album Dark Sky Paradise" / "Start the record Illuminate" / "Let's hear the album Come Away With Me by Norah Jones"
- By Song: "Play the track Come As You Are" / "Put on the track Billie Jean" / "Play the song Pour It Up" / "Put on the song Holy Grail" / "Let's listen the song Mercy" (sic)
- Playlists: "Play the playlist Christmas Tunes" / "Shuffle the playlist Party Music"

Player controls: Play / Resume / Pause / Stop / Next / Previous / Shuffle / Loop.

Status note (third-party, labeled): itechguides.com states "Plex disabled the official Plex Alexa Skill on June 15, 2026"; consistent with the emptied live KB, but UNCONFIRMED from any Plex-primary page. Treat the date as a third-party claim.

## 5. Music Assistant (source class: project docs + code)

**Finding: Music Assistant HAS an Alexa integration**, shipped as a community "Alexa player provider" plus a self-hosted custom skill users build themselves (invocation "music assistant"). It is NOT a store skill and documents NO per-content grammar (no play-artist/album/song utterances anywhere).

- Official docs page music-assistant.io/player-support/alexa/ (source music-assistant.io repo `src/content/docs/player-support/alexa.md`): "Under Interaction Model → Intents add an intent named `PlayAudio` with sample utterances such as `play audio`, `start`, and `play` and then build the model." "Not Yet Supported" verbatim: "Multi-room synchronized playback" / "Advanced playback features (e.g., shuffle, repeat, crossfade)". The docs reference the prototype repo github.com/alams154/music-assistant-alexa-skill-prototype.
- Server provider source (music-assistant/server `music_assistant/providers/alexa/__init__.py`), verbatim launch wrappers per locale (ALEXA_LANGUAGE_COMMANDS): "ask music assistant to play audio" (en-US/en-CA/default) / "sag music assistant spiele audio" (de-DE) / "pídele a music assistant que reproduzca audio" (es-ES) / "chiedi a music assistant di riprodurre audio" (it-IT) / "peça ao music assistant para reproduzir áudio" (pt-BR) / "speel music assistant" (nl-NL) / "music assistant" (fr-CA/fr-FR). Handler mappings verbatim: STOP -> AMAZON.StopIntent, PLAY -> AMAZON.ResumeIntent, PAUSE -> AMAZON.PauseIntent.

Relevance to us: MA's es-ES wrapper "pídele a X que reproduzca audio" and it-IT wrapper "chiedi a X di riprodurre audio" are the SAME wrapper architectures as our es subjunctive wrapper family (JF-551: "pide a X que + subjunctive") and our it-IT "Di riprodurre" infinitive wrappers. Independent official confirmation that both wrapper families are idiomatic. The prototype's models (mined in section 8.6) carry only transport/start phrasings.

## 6. Emby official Alexa documentation (source class: vendor docs)

https://emby.media/support/articles/Alexa.html (fetched 2026-11-25; linked from the official Apps articles page). All verbatim.

Invocation: "Every command needs to start with: Alexa, tell/ask m. b. home -to [insert command]" and "Alexa, - start/begin/open m. b. home". Escape: "Alexa, cancel/ never-mind".

Media types: "episode, movie, show, season, song, album, artist, audiobook, channel, playlist".

Media playback grammar (page's own notation): play (the movie) {movie} / player (the show) {series} (sic) / resume the episode from {series} / play the artist {Artist} / play the song {song} (from {Album}, by {Artist}) / play the audiobook {title} / tune in channel {name}.

Examples: "put on season 2 of Game of Thrones" / "play Supernatural, season 5, episode 12" / "play the new episode of Gotham" / "watch the next episode of Orange is the new black".

Random music: "drop the beat" / "put on some music (genre {Genre})" / "play songs (genre {Genre})" / "play songs by {Artist}" / "play songs from the album {Album}".

Recently added: "what's new" / "what's been recently added?" / "what {content type} is newly added".

Continue playing: "what can I keep watching?" / "what (show/movie/audio book) was I in the middle of?".

Suggestions: "give me a suggestion" / "suggest me a ({genre}) {content type}" / "I don't know what ({episode}) to watch" / "what's good?".

Playback: pause/previous/next/stop / "seek to {time} or start from the beginning" / "jump to chapter {number}" / "go to the next/previous chapter" / "set the volume (to {percent})".

Note: Emby's grammar carries "play songs by {Artist}" (by-order again), "play the song {song} (from {Album}, by {Artist})" (dual scoping), "play the new episode of {show}" and "watch the next episode of {show}" (both next-episode phrasings).

## 7. My Media for Alexa official voice-command list (source class: vendor docs, 5 locales)

https://www.mymediaalexa.com/home/commands (fetched 2026-11-25). Header verbatim: "Please use this voice command guide to help you get the most out of My Media for Alexa." This is our DIRECT commercial ancestor; locales offered: English, German, Italian, French, Spanish. Verbatim highlights (wrapper "Alexa, ask My Media to ..." elided where constant):

**English**: "play the album {Album}" / "play {TrackName}" / "play the track {TrackName}" / "play the song {TrackName}" / "play {TrackName} by {Artist}" / "play the track {TrackName} by {Artist}" / "play the song {TrackName} by {Artist}" / "play {Music} by {Artist}" / "play {Artist}" / "play the artist {Artist}" / "play my {Playlist} playlist" / "play {Genre} {Music}" / "play some {Genre} {Music}" / "turn looping on/off" / "turn shuffle on/off" / "shuffle the album {Album}" (UK only) / "shuffle {Music} from the album {Album}" / "shuffle {Music} by {Artist}" / "shuffle {Artist}" / "shuffle my {Playlist} playlist" / "shuffle some {Genre} {Music}" / "read {BookName}" / "what's playing" / "add this song to my {Playlist} playlist" / "ignore this song" / "list my servers".

**Italian** (page's own phrasing; wrapper "Alexa, apri My Media di ..." constant): "di riprodurre l'album {Album}" / "di riprodurre {Music} dall'album {Album}" / "di riprodurre la traccia {TrackName}" / "di riprodurre la canzone {TrackName}" / "di riprodurre {TrackName} di {Artist}" / "di riprodurre la canzone {TrackName} prodotta da {Artist}" / "di riprodurre {Artist}" / "di riprodurre la playlist {Playlist}" / "di riprodurre la mia playlist {Playlist}" / "di riprodurre {Genre} {Music}" / "di riprodurre un po di {Genre} {Music}" / "di suonare della {Music} {Genre}" / "di attivare la ripetizione" / "di ripetere il brano corrente" / "di attivare la riproduzione casuale" / "di attivare la modalità casuale" / "di ascoltare in modalità casuale" / "di riprodurre l'album {Album} a caso" / "di riprodurre {Music} di {Artist} a caso" / "di riprodurre un po di {Genre} {Music} a caso".

**German**: "frag Meine Medien um das Album {Album} zu spielen" / "frag Meine Medien Spiele das Album {Album}" / "frag Meine Medien Spiele {TrackName} von {Artist}" / "frag Meine Medien Shuffle ausschalten" / "frag Meine Medien Loop abschalten" / "frag Meine Medien Lies {BookName}".

**French**: "demande à My Media joue l'album {Album}" / "joue {TrackName}" / "joue {TrackName} de {Artist}" / "joue {Artist}" / "joue ma playlist {Playlist}" / "joue {Genre} {Music}" / "joue en boucle" / "joue à l'infini" / "active la répétition" / "joue en aléatoire" / "active la lecture aléatoire" / "éteint la lecture aléatoire".

**Spanish**: "abre My Media reproduzca el álbum {Album}" / "abre My Media ponga el álbum {Album}" / "abre My Media reproduzca {TrackName}" / "abre My Media ponga la canción {TrackName}" / "abre My Media reproduzca {TrackName} de {Artist}" / "abre My Media ponga {Artist}" / "abre My Media reproduzca mi playlist {Playlist}" / "abre My Media reproduzca {Genre} {Music}".

Cross-locale observations for the delta: the es rows use SUBJUNCTIVE verbs after the wrapper ("reproduzca", "ponga"), the it rows use the "di + infinitive" wrapper ("di riprodurre", "di suonare"), and the de rows use bare imperatives ("Spiele ... von {Artist}").

## 8. Open-source media-skill corpus (GitHub)

All fetched 2026-11-25 from raw.githubusercontent.com; provenance from the GitHub API.

### 8.1 andresponte/askplex (Plex skill clone, 17 locales)

"AskPlex is an Alexa skill that allows you to play music hosted by your Plex Media Server (PMS). The official Plex skill is not available in all regions, so this skill serves as an alternative." Created 2025-02-21, last push 2026-10-24. Slot types: AMAZON.Artist, AMAZON.MusicAlbum, AMAZON.MusicRecording, AMAZON.Genre, custom playlist_names. Transport built-ins EMPTY. `es-US.json` and `es-ES.json` are byte-identical (diff-verified).

**en-US**: `PlayMusicByArtist`: "Play {artist}" / "Play the artist {artist}" / "Play the singer {artist}" / "Play the band {artist}" / "Play songs by artist {artist}" / "Play songs by the artist {artist}" / "Play songs by the singer {artist}" / "Play songs by the band {artist}" / "Play music by artist {artist}" / "Play music by the artist {artist}" / "Play music by the singer {artist}" / "Play music by the band {artist}" (plus singer/band article-less variants). `PlaySongByArtist`: "Song {song} by {artist}" / "Play {song} by {artist}" / "Play the song {song} by {artist}" / "Play the song {song} by artist {artist}" / "Play the song {song} by singer {artist}" / "Play the song {song} by band {artist}" / "Play the song {song} by the artist {artist}" / "Play the song {song} by the singer {artist}" / "Play the song {song} by the band {artist}". `PlayAlbumByArtist`: "Song {album} by {artist}" / "Play {album} by {artist}" / "Play the album {album} by {artist}" / "Play the album {album} by artist {artist}" / "Play the album {album} by singer {artist}" / "Play the album {album} by band {artist}" / "Play the album {album} by the artist {artist}" / "Play the album {album} by the singer {artist}" / "Play the album {album} by the band {artist}". `PlayRandomMusic`: "Play music" / "Play some music" / "Play random music" / "Play random songs" / "Play random tracks". `PlayMusicByGenre`: "Genre {genre}" / "Music {genre}" / "Music from the genre {genre}" / "Play songs from the genre {genre}" / "Play genre {genre}" / "Play music {genre}" / "Play music from the genre {genre}". `PlayPlaylist`: "Playlist {playlist}" / "Play the playlist {playlist}". `PlaybackSongDetails`: "What is playing" / "What song is this" / "What is playing right now" / "What is the name of this song" / "What is the name of the song" / "What is the name of the song that is playing" / "What is this" / "Who is this song by" / "Who is the song by" / "Who is singing this" / "Who is singing".

**es-ES**: `PlayMusicByArtist`: "pon {artist}" / "pon a {artist}" / "pon al artista {artist}" / "pon al cantante {artist}" / "pon a la banda {artist}" / "pon al grupo {artist}" / "pon canciones del artista {artist}" / "pon canciones del cantante {artist}" / "pon canciones de la banda {artist}" / "pon canciones del grupo {artist}" / "pon música del artista {artist}" / "pon música del cantante {artist}" / "pon música de la banda {artist}" / "pon música del grupo {artist}" / "reproduce canciones del artista {artist}" / "reproduce canciones del cantante {artist}" / "reproduce canciones de la banda {artist}" / "reproduce canciones del grupo {artist}" / "reproduce música del artista {artist}" / "reproduce música del cantante {artist}" / "reproduce música de la banda {artist}" / "reproduce música del grupo {artist}". `PlaySongByArtist`: "canción {song} de {artist}" / "pon {song} de {artist}" / "pon la canción {song} de {artist}" / "pon la canción {song} del artista {artist}" / "pon la canción {song} del cantante {artist}" / "pon la canción {song} de la banda {artist}" / "pon la canción {song} del grupo {artist}" / "reproduce {song} de {artist}" / "reproduce canción {song} de {artist}" / "reproduce la canción {song} de {artist}" / "reproduce la canción {song} del artista {artist}" / "reproduce la canción {song} del cantante {artist}" / "reproduce la canción {song} de la banda {artist}" / "reproduce la canción {song} del grupo {artist}". `PlayAlbumByArtist`: "disco {album} de {artist}" / "álbum {album} de {artist}" / "pon el disco {album} de {artist}" / "pon el álbum {album} de {artist}" / "reproduce el disco {album} de {artist}" / "reproduce el álbum {album} de {artist}". `PlayRandomMusic`: "pon música" / "pon música aleatoria" / "pon canciones aleatorias" / "reproduce música aleatoria" / "reproduce canciones aleatorias". `PlayMusicByGenre`: "género {genre}" / "música {genre}" / "música del género {genre}" / "pon canciones del género {genre}" / "pon género {genre}" / "pon música {genre}" / "pon música del género {genre}" / "reproduce género {genre}" / "reproduce música {genre}" / "reproduce música del género {genre}" / "reproduce canciones del género {genre}". `PlayPlaylist`: "lista {playlist}" / "lista de reproducción {playlist}" / "pon la lista {playlist}" / "pon la lista de reproducción {playlist}" / "reproduce la lista {playlist}" / "reproduce la lista de reproducción {playlist}". `PlaybackSongDetails`: "qué está sonando" / "qué canción es esta" / "qué está sonando ahora" / "cómo se llama esta canción" / "cómo se llama la canción" / "cómo se llama la canción que está sonando" / "qué es esto" / "cuál es el nombre de esta canción" / "cuál es el nombre de la canción" / "de quién es esta canción" / "de quién es la canción" / "quién canta esto" / "quién está cantando".

**it-IT**: `PlayMusicByArtist`: "Riproduci {artist}" / "Riproduci l'artista {artist}" / "Riproduci il cantante {artist}" / "Riproduci la band {artist}" / "Riproduci canzoni di {artist}" / "Riproduci canzoni del cantante {artist}" / "Riproduci canzoni della band {artist}" / "Riproduci musica di {artist}" / "Riproduci musica del cantante {artist}" / "Riproduci musica della band {artist}". `PlaySongByArtist`: "Canzone {song} di {artist}" / "Riproduci {song} di {artist}" / "Riproduci la canzone {song} di {artist}" / "Riproduci la canzone {song} del cantante {artist}" / "Riproduci la canzone {song} della band {artist}" / "Riproduci la canzone {song} dell'artista {artist}". `PlayAlbumByArtist`: "Album {album} di {artist}" / "Riproduci {album} di {artist}" / "Riproduci l'album {album} di {artist}" / "Riproduci l'album {album} dell'artista {artist}" / "Riproduci l'album {album} del cantante {artist}" / "Riproduci l'album {album} della band {artist}". `PlayRandomMusic`: "Riproduci musica" / "Riproduci della musica" / "Riproduci musica casuale" / "Riproduci canzoni casuali" / "Riproduci brani casuali". `PlaybackSongDetails`: "Cosa sta suonando" / "Che canzone è questa" / "Cosa sta suonando adesso" / "Qual è il nome di questa canzone" / "Qual è il nome della canzone" / "Qual è il nome della canzone che sta suonando" / "Cos'è questo" / "Chi canta questa canzone" / "Chi è l'artista di questa canzone" / "Chi sta cantando".

**fr-FR**: `PlayMusicByArtist`: "Joue {artist}" / "Joue l'artiste {artist}" / "Joue le chanteur {artist}" / "Joue le groupe {artist}" / "Joue des chansons de l'artiste {artist}" / "Joue des chansons du chanteur {artist}" / "Joue des chansons du groupe {artist}" / "Joue de la musique de l'artiste {artist}" / "Joue de la musique du chanteur {artist}" / "Joue de la musique du groupe {artist}". `PlaySongByArtist`: "La chanson {song} de {artist}" / "Joue {song} de {artist}" / "Joue la chanson {song} de {artist}" / "Joue la chanson {song} de l'artiste {artist}" / "Joue la chanson {song} du chanteur {artist}" / "Joue la chanson {song} du groupe {artist}". `PlayAlbumByArtist`: "L'album {album} de {artist}" / "Joue {album} de {artist}" / "Joue l'album {album} de {artist}" / "Joue l'album {album} de l'artiste {artist}" / "Joue l'album {album} du chanteur {artist}" / "Joue l'album {album} du groupe {artist}". `PlayMusicByGenre`: "Genre {genre}" / "Musique {genre}" / "Musique du genre {genre}" / "Joue des chansons du genre {genre}" / "Joue le genre {genre}" / "Joue de la musique {genre}" / "Joue de la musique du genre {genre}". `PlaybackSongDetails`: "Qu'est-ce qui joue" / "Quelle chanson est-ce" / "Qu'est-ce qui joue en ce moment" / "Quel est le nom de cette chanson" / "Quel est le nom de la chanson" / "Quel est le nom de la chanson qui joue" / "Qu'est-ce que c'est" / "Qui chante cette chanson" / "Qui chante".

**de-DE**: `PlayMusicByArtist`: "Spiele {artist}" / "Spiele den Künstler {artist}" / "Spiele den Sänger {artist}" / "Spiele die Band {artist}" / "Spiele Lieder von {artist}" / "Spiele Lieder des Sängers {artist}" / "Spiele Lieder der Band {artist}" / "Spiele Musik von {artist}" / "Spiele Musik des Sängers {artist}" / "Spiele Musik der Band {artist}". `PlaySongByArtist`: "Lied {song} von {artist}" / "Spiele {song} von {artist}" / "Spiele das Lied {song} von {artist}" / "Spiele das Lied {song} von Künstler {artist}" / "Spiele das Lied {song} vom Sänger {artist}" / "Spiele das Lied {song} von der Band {artist}". `PlayAlbumByArtist`: "Album {album} von {artist}" / "Spiele {album} von {artist}" / "Spiele das Album {album} von {artist}" / "Spiele das Album {album} von Künstler {artist}" / "Spiele das Album {album} vom Sänger {artist}" / "Spiele das Album {album} von der Band {artist}". `PlayRandomMusic`: "Spiele Musik" / "Spiele etwas Musik" / "Spiele zufällige Musik" / "Spiele zufällige Lieder" / "Spiele zufällige Tracks". `PlaybackSongDetails`: "Was läuft gerade" / "Welches Lied ist das" / "Was wird gerade gespielt" / "Wie heißt dieses Lied" / "Wie heißt das Lied" / "Wie heißt das Lied, das gerade läuft" / "Was ist das" / "Wer singt dieses Lied" / "Wer ist der Künstler dieses Liedes" / "Wer singt das".

### 8.2 rosskouk/asknavidrome (Navidrome/Subsonic, en)

"An Alexa skill to allow streaming of music from Subsonic API compatible media servers on Amazon Echo devices." (push 2026-03). Invocation "navisonic". `NaviSonicPlayMusicByArtist`: "play songs by {artist}" / "play music by {artist}". `NaviSonicPlayAlbumByArtist`: "Play the album {album}" / "Play the album {album} by {artist}". `NaviSonicPlayPlaylist`: "Play the {playlist} playlist" / "Start the {playlist} playlist". `NaviSonicPlaySongByArtist`: "Play the song {song} by the band {artist}" / "Play {song} by the band {artist}" / "Play {song} by {artist}" / "Play the song {song} by the artist {artist}". `NaviSonicPlayFavouriteSongs`: "Play my starred tracks" / "Play starred tracks" / "Play starred songs" / "Play my starred songs" / "Play my favourite songs". `NaviSonicPlayMusicByGenre`: "Play {genre} songs" / "Play {genre} music". `NaviSonicPlayMusicRandom`: "Play a selection of music" / "Play a mix of tracks" / "Play a mix of songs" / "Play random music" / "Play random songs". `NaviSonicRandomiseQueue`: "randomise the queue" / "randomise" / "shuffle" / "shuffle the queue". `NaviSonicSongDetails`: "What is playing" / "Who is singing" / "Who's singing" / "What album is this song on" / "What album is this on" / "Which album" / "Which album is this song from" / "Which artist is this" / "What band is this" / "Who sings this song" / "Who sings this" / "What song is this" / "What's playing". `NaviSonicStarSong`: "Add this song to my favourites" / "Favourite this song" / "Like this song" / "Add this song to my liked songs" / "Star this song". `NaviSonicUnstarSong`: "Remove this song from favourites" / "I don't like this song" / "Remove the star from this song" / "Delete this song from my favourites" / "Unstar this song".

### 8.3 Rusketh/JellyMusic (Jellyfin music skill, en + es)

"A work in progress Alexa Skill for Jellyfin music" (push 2026-05). Invocation "jelly music". Slots: AMAZON.MusicAlbum/Musician/MusicRecording/Genre.

**EN**. PlaySongIntent: "play song {songname} by {artistname}" / "play {songname} by {artistname}" / "play {songname}" / "play the song {songname}" / "play the track {songname}" / "put on the song {songname}" / "I want to hear the song {songname}" / "play the track {songname} by {artistname}" / "put on {songname} by {artistname}" / "start the song {songname}" / "let me hear {songname}". PlayAlbumIntent: "play the album {albumname} by {artistname}" / "play the album {albumname}" / "play album {albumname} by {artistname}" / "play album {albumname}" / "put on the album {albumname}" / "I want to listen to the album {albumname}" / "start the album {albumname}" / "spin the album {albumname}" / "play the record {albumname}" / "play the full album {albumname}" / "listen to the album {albumname} by {artistname}" / "can you play the album {albumname}". PlayArtistIntent: "play songs by {artistname}" / "play artist {artistname}" / "play music by {artistname}" / "put on {artistname}" / "I want to hear {artistname}" / "listen to {artistname}" / "play {artistname}" / "start playing {artistname}" / "put on some {artistname}" / "give me some {artistname}" / "play a mix of {artistname}" / "shuffle music by {artistname}" / "put on music from {artistname}". PlayPlaylistIntent: "play playlist {playlistname}" / "start playlist {playlistname}" / "load playlist {playlistname}" / "put on my {playlistname} playlist" / "play the {playlistname} playlist" / "listen to playlist {playlistname}" / "queue up playlist {playlistname}" / "play my playlist called {playlistname}" / "start the {playlistname} playlist". PlayGenreIntent: "play {genre} music" / "play genre {genre}" / "play some {genre}" / "put on some {genre} music" / "start playing {genre}" / "I feel like listening to {genre}" / "play me some {genre}". ShuffleAlbumIntent: "shuffle the album {albumname}" / "shuffle the album {albumname} by {artistname}" / "shuffle album {albumname} by {artistname}" / "shuffle album {albumname}" / "play album {albumname} on shuffle" / "mix the album {albumname}" / "play the album {albumname} shuffled". ShuffleArtistIntent: "shuffle artist {artistname}" / "shuffle songs by {artistname}" / "play {artistname} on shuffle" / "mix up songs by {artistname}" / "play a random mix of {artistname}" / "shuffle music from {artistname}" / "play random songs by {artistname}". ShufflePlaylistIntent: "shuffle playlist {playlistname}" / "play playlist {playlistname} on shuffle" / "mix up playlist {playlistname}" / "shuffle the {playlistname} playlist" / "play my {playlistname} playlist on shuffle". QueueSongIntent: "add {songname} to the queue" / "add song {songname} to the queue" / "add {songname} by {artistname} to the queue" / "add song {songname} by {artistname} to the queue" / "queue {songname}" / "queue song {songname}" / "queue {songname} by {artistname}" / "queue song {songname} by {artistname}" / "play {songname} next" / "put {songname} on the queue" / "add the track {songname} to the queue". QueueAlbumIntent: "add the album {albumname} by {artistname} to the queue" / "queue the album {albumname}" / "queue album {albumname} by {artistname}" / "play album {albumname} next" / "put album {albumname} in the queue" / "add {albumname} to the up next list". QueueArtistIntent: "add songs by {artistname} to the queue" / "queue artist {artistname}" / "queue songs by {artistname}" / "play {artistname} next" / "add {artistname} to the up next" / "put {artistname} in the queue" / "add music by {artistname} to the queue". PlayFavouritesIntent: "play favourites for {username}" / "play favorites for {username}" / "from {username} play favourites" / "play {username} favourite tracks" / "play {username} favourite songs" / "play {username} favourite music" / "play {username} favourites" / "put on {username} favourites" / "listen to {username} favourites" / "start {username} favourites" / "play the favourites list for {username}" / "play {username} top tracks" / "play {username} liked songs" / "put on the favorites for {username}".

**ES** (skill-ES.json): same intent set; PlaySongIntent uses infinitive-first carriers "reproducir canción {songname}" / "escuchar canción {songname}" / "poner canción {songname}" (full list retained in /tmp/jf963/models/jellymusic_ES.json).

### 8.4 m0ngr31/koko (Kodi music streaming, en + de + it)

"Alexa skill for streaming music from Kodi" (push 2020; mature). Speech assets SampleUtterances.{en,de,it}.txt (es/fr files empty in-repo).

**en**. StreamSong: "play song {Song}" / "play song {Song} by {Artist}" / "play the song {Song}" / "play the song {Song} by {Artist}". StreamAlbum: "play album {Album}" / "play album {Album} by {Artist}" / "play the album {Album}" / "play the album {Album} by {Artist}". StreamAlbumOrSong: "play the {Album} by {Artist}" / "play the {Song} by {Artist}" / "play {Album} by {Artist}" / "play {Song} by {Artist}" (deliberate ambiguity intent). StreamArtist: "play artist {Artist}" / "play music by {Artist}". StreamAudioPlaylist: "play playlist {AudioPlaylist}" / "play the playlist {AudioPlaylist}" / "play music playlist {AudioPlaylist}" / "play {AudioPlaylist} playlist" (12 shapes, noun-before and after). StreamAudioPlaylistRecent: "play recent albums" / "play recently added albums" / "play recently added music" / "play recently added songs". StreamPartyMode: "play all music" / "play music" / "play random music". StreamThis: "play current playlist" / "play this".

**de**. StreamAlbum: "spiel das Album {Album}" / "spiel das Album {Album} von {Artist}" / "spiel die CD {Album}" / "spiel die Platte {Album}" (plus "spiele" variants). StreamSong: "spiel das Lied {Song} von {Artist}" / "spiel den Song {Song} von {Artist}". StreamArtist: "spiel Musik von {Artist}" / "spiel {Artist}". StreamPartyMode: "spiel Musik" / "spiel Lieder" / "spiel Mucke" / "spiel irgendwelche Musik" / "spiel zufällige Lieder".

**it** (356-line corpus; representative). StreamAlbum: "esegui album {Album}" / "esegui album {Album} di {artist}" / "esegui l' album {Album}" / "metti album {Album}" (plus the full degli/dei/delle/di agreement grid). StreamAlbumOrSong: "esegui {Album} di {artist}" / "esegui {Song} degli {artist}" / "metti su {Album} di {artist}". StreamArtist: "esegui musica di {artist}" / "metti musica di {artist}" / "metti su musica di {artist}" / "suona musica di {artist}". StreamPartyMode: "esegui musica" / "esegui tutta la musica" / "esegui musica a caso" / "metti musica" / "metti su musica" / "suona musica". StreamAudioPlaylistRecent: "esegui album aggiunti da poco" / "esegui canzoni aggiunte di recente" / "esegui brani recenti" (long recency-synonym family).

### 8.5 m0ngr31/kanzi (Kodi control, en + de + es + fr + it)

"Alexa skill for controlling Kodi". Utterance files 1.3k to 3.8k lines per locale. Extracted verbatim, play-relevant intents only.

**en**. ListenToSong: "play song {Song}" / "play the song {Song}" / "listen to song {Song}" / "listen to the song {Song}" (plus "by {Artist}", "from album {Album}", "from the album {Album}", "on album {Album}", "on the album {Album}" variants of each). ListenToAlbum: "play album {Album}" / "play the album {Album}" / "listen to album {Album}" / "listen to the album {Album}" (plus "by {Artist}"). ListenToArtist: "play artist {Artist}" / "play music by {Artist}" / "listen to artist {Artist}" / "listen to music by {Artist}" / "shuffle artist {Artist}" / "shuffle music by {Artist}" (plus MusicGenre-qualified variants). ListenToGenre: "play {MusicGenre} music" / "play {MusicGenre} songs" / "play {MusicGenre} genre" (plus listen/shuffle variants). WatchMovie: "play movie {Movie}" / "play the movie {Movie}" / "play film {Movie}" / "play the film {Movie}" / "watch movie/film {Movie}". WatchEpisode: "play episode {Episode} season {Season} of {Show}" / "play season {Season} episode {Episode} of {Show}" / "play {Show} season {Season} episode {Episode}" / "play {Show} episode {Episode} season {Season}" (plus watch variants; "series" as season synonym). WatchNextEpisode: "play next episode of {Show}" / "play the next episode of {Show}" (plus watch variants). RecommendSong: "recommend a song" / "recommend some music" / "recommend music" / "recommend song" (plus "to listen to" suffix family). RecommendArtist: "recommend an artist" / "recommend a band" / "recommend a musician" / "recommend a music artist". ViewAlbums: "show albums" / "show music albums" / "open albums" / "go to albums" / "go to music albums". ViewArtists: "show artists" / "show musicians" / "open artists" / "open musicians" / "go to artists" / "go to musicians". WhatNewAlbums: "are there new albums" / "do i have new albums" / "what new albums are there" / "what new albums do i have".

**es**. ListenToSong: "escuchar cancion {Song}" / "escuchar la cancion {Song}" / "reproducir cancion {Song}" / "reproducir la cancion {Song}" (plus "de {Artist}", "del album {Album}"). ListenToAlbum: "escuchar album {Album}" / "escuchar el album {Album}" / "reproducir album {Album}" / "reproducir el album {Album}". ListenToArtist: "escuchar {Artist}" / "escuchar de {Artist}" / "escuchar musica de {Artist}" / "barajar {Artist}" / "barajar musica de {Artist}". ListenToGenre: "escuchar musica {MusicGenre}" / "escuchar canciones {MusicGenre}" / "escuchar genero {MusicGenre}" / "reproducir ..." / "barajar ...". WatchMovie: "reproducir pelicula {Movie}" / "reproducir la pelicula {Movie}" / "ver ..." (grid; colloquial "peli"). WatchEpisode: "reproducir episodio {Episode} de la temporada {Season} de la serie {Show}" / "reproducir temporada {Season} episodio {Episode} de {Show}" / "reproducir {Show} temporada {Season} episodio {Episode}" (plus ver variants). WatchNextEpisode: "reproducir el proximo episodio de {Show}" / "reproducir el siguiente episodio de {Show}" / "ver ...".

**fr**. ListenToSong: "joue la chanson {Song}" / "joue la musique {Song}" (plus "de {Artist}", "d' {Artist}", "par {Artist}", "de l'album {Album}", "sur l'album {Album}"). ListenToAlbum: "joue l'album {Album}" / "lis l'album {Album}" / "écoute l'album {Album}" (plus "de/d'/par {Artist}"). ListenToArtist: "joue de la musique de {Artist}" / "joue de la musique par {Artist}". ListenToGenre: "joue de la musique {MusicGenre}" / "joue du {MusicGenre}" / "joue des chansons {MusicGenre}" / "lis ..." / "mélange de la musique {MusicGenre}". WatchMovie: "joue le film {Movie}" / "lis le film {Movie}" / "regarde le film {Movie}" / "démarre le film {Movie}". WatchEpisode: "joue l'épisode {Episode} de la saison {Season} de {Show}" / "joue {Show} saison {Season} épisode {Episode}" (plus lis/regarde/démarre grids). WatchNextEpisode: "joue l'épisode suivant de {Show}" / "joue le prochain épisode de {Show}". ViewAlbums: "affiche les albums" / "montre les albums" / "ouvre les albums" / "va les albums". WhatNewAlbums: "ai-je de nouveaux albums" / "est-ce qu'il y a de nouveaux albums" / "quels nouveaux albums ai-je" family.

**de**. ListenToSong: "spiel das Lied {Song}" / "spiel den Song {Song}" (plus "von {Artist}", "vom Album {Album}", "aus dem Album {Album}", "auf dem Album {Album}"). ListenToAlbum: "spiel das Album {Album}" / "spiel die CD {Album}" / "spiel die Platte {Album}" (plus "von {Artist}"). ListenToArtist: "spiel Musik von {Artist}" / "spiel Musik von dem Artist {Artist}" / "von dem Interpreten {Artist}" / "von dem Komponisten {Artist}" / "von dem Künstler {Artist}" / "von der Band {Artist}" / "von der Gruppe {Artist}" (plus feminine Künstlerin/Komponistin forms). ListenToGenre: "spiel {MusicGenre} Musik" / "spiel {MusicGenre} Songs" / "spiel {MusicGenre} Genre". WatchMovie: "zeige Film {Movie}" (only verb). WatchEpisode: "zeige Staffel {Season} Episode {Episode} von {Show}" / "zeige Staffel {Season} Folge {Episode} von {Show}". WatchNextEpisode: "zeige nächste Episode von {Show}" / "zeige nächste Folge von {Show}". ViewAlbums: "gehe zu Alben" / "öffne Alben".

**it**. ListenToSong: "ascolta brano {Song}" / "ascolta canzone {Song}" (plus "di {artist}", "dall' album {Album}", "nell' album {Album}", agreement grid). ListenToAlbum: "ascolta album {Album}" / "ascolta l'album {Album}" (plus agreement grid). ListenToArtist: "ascolta brani di {artist}" / "ascolta brani {MusicGenre} di {artist}" (plus agreement grid).

### 8.6 alams154/music-assistant-alexa-skill-prototype (the MA Alexa surface)

"This project is an Alexa skill prototype for controlling the Music Assistant server..." (push 2026-10-15). Independent prototype, referenced by MA's own docs (section 5). **Finding: no song/artist/album/playlist intents exist.** Only a start-playback intent plus built-in transport:

- en-US (invocation "my radio"), PlayAudio: "play" / "start" / "play my radio" / "start my radio" / "start the radio" / "start the audio" / "play the audio" / "start the music" / "play the music" (plus please variants).
- es-ES ("mi radio"): "jugar" / "poner" / "reproducir" / "reproducir mi radio" / "pon mi radio" / "pone mi radio" / "pon la radio" / "poner la música". CAUTION: "jugar" is the game-verb mistranslation; machine-translated noise, not an alignment target.
- it-IT ("la mia radio"): "giocare" / "avviare la radio" / "metti la radio" / "accendi la radio" / "metti la musica". Same "giocare" caveat.
- fr-FR ("ma radio"): "joue" / "jouer" / "démarre" / "lance le direct" / "lance la radio" / "joue la musique" / "lance la musique".
- de-DE ("mein radio"): "spiele" / "starte" / "spiele mein radio" / "starte das radio" / "starte die wiedergabe" / "starte die musik".
- built-in transport (en-US): NextIntent "next"/"skip"/"skip forward"; PreviousIntent "go back"/"skip back"/"back up"; ResumeIntent "resume"/"continue"/"keep going"; LoopOnIntent "loop"/"loop on"/"keep playing this"; ShuffleOnIntent "shuffle"/"shuffle on"/"shuffle the music"/"shuffle mode"; ShuffleOffIntent "stop shuffling"/"shuffle off"/"turn off shuffle"; StartOverIntent "start over"/"restart"/"start again"; StopIntent "stop"/"off"/"shut up"; PauseIntent "pause"/"pause that"; RepeatIntent "repeat"/"say that again"/"repeat that"; CancelIntent "cancel"/"never mind"/"forget it"; plus AMAZON.SelectIntent ordinal picks ("get top result" / "get last result" / "select third entry") and AMAZON.MoreIntent.

Note: unlike the media-server family, this prototype attaches custom samples to built-in transport intents (matching the official Common Utterances, section 3.4). The media-server family (askplex, asknavidrome, koko/kanzi, ours) leaves them EMPTY.

### 8.7 infinityofspace/jellyfin_alexa_skill (our upstream, en)

Invocation "jellyfin player". A strict subset of our en-US model. Baseline rows: PlayArtistSongsIntent "Play songs from {musician}" / "Play music from {musician}"; PlaySongIntent "Play the song {song}" / "Play the song {song} from {musician}"; PlayAlbumIntent "Play the album {album}" / "Play the album {album} by {musician}"; StartOverIntent "Repeat from beginning" / "Start over".

### 8.8 squeeze-alexa (LMS, en_GB/en_US) and My Media wrappers

squeeze-alexa: volume/player-oriented; carryover small ("what song is this", "what is playing", "play some {Genre}", "play a mix of {Genre} and {SecondaryGenre}"). My Media for Alexa itself is closed-source; GitHub holds only wrapper projects. Its official multilingual command list is mined in section 7.

### 8.9 Community Plex bridge (third-party, falk0069/plex-alexa-skill-bridge)

README command list verbatim: "Alexa, ask Plex to play the artist Creed" / "play the album Dirt" / "play the song Africa" / "play the playlist Road Trip" / "shuffle Fleetwood Mac" / "play music from the 1990s" / "play some Rock" / "play music" / "play my most played music" / "play recently added music". Platform note verbatim: "Alexa's invocation model requires \"ask Plex to...\" - natural music commands like \"Alexa, play X on Plex\" are reserved for Amazon Music partners".

## 9. Consolidated phrasing corpus

Frequency counts now span 10 evidence tiers: Amazon MSAPI docs (A), Plex official (P), Emby official (E), My Media official (M), plus the 6 open-source families (askplex, asknavidrome, JellyMusic, koko, kanzi, MA-prototype). The official tiers outrank the open-source ones.

### 9.1 Play song

| Shape | Evidence |
|---|---|
| "play the song {s}" / "play song {s}" | A (official MSAPI), P, M, kanzi, koko, JellyMusic |
| "play {s} by {a}" / "play the song {s} by {a}" / "play track {t} by {a}" | A, P, M, E, all six families |
| "play the track {t}" / "put on the track {t}" | P (official), ours, JellyMusic, kanzi |
| "play song {s} from {album}" (song scoped by album) | A (official MSAPI + API reference), kanzi en, MyMedia it ("dall'album"), kanzi de/it |
| "put on {s}", "listen to {s}", "I want to hear the song {s}", "start the song {s}", "let me hear {s}" | P/M vocabulary, JellyMusic |
| verbless noun carriers ("Song {s} by {a}", "canción {s} de {a}") | askplex only |

### 9.2 Play artist

| Shape | Evidence |
|---|---|
| **"play music by {a}" / "play songs by {a}"** | A (official MSAPI), P ("Put on music by Bob Marley", "Play music by Adele", "I want to hear songs by Avicii"), E, M, askplex, asknavidrome, JellyMusic, koko, kanzi: the universal order |
| "play the artist {a}" / "play artist {a}" | P ("Play the artist Rihanna"), M ("play the artist {Artist}"), E ("play the artist {Artist}"), askplex, kanzi, koko |
| "shuffle music by {a}" / "shuffle {a}" | P ("Shuffle music by Pink Floyd", "Shuffle Queen"), M, JellyMusic, kanzi |
| "let's hear music by {a}" / "I want to hear songs by {a}" | P, A variation shapes |
| "play a mix of {a}" / "play random songs by {a}" | JellyMusic |

### 9.3 Play album

| Shape | Evidence |
|---|---|
| "play the album {al} by {a}" / "play the album {al}" | A, P, M, E, all families |
| "put on the album {al} by {a}" | P ("Put on the album Nevermind by Nirvana"), ours, JellyMusic |
| "start the album {al}" / "start the record {al}" | P ("Start the record Illuminate"), JellyMusic ("start the album") |
| "let's hear the album {al} by {a}" / "I want to hear the album {al}" | P, JellyMusic |
| "shuffle the album {al}" | P ("Shuffle the album Thriller"), M, JellyMusic |
| album synonyms: "record" (ours, JellyMusic, P), "CD"/"Platte" (koko-de, kanzi-de), "disco" (askplex-es, ours es) | family-specific |
| "play the full album {al}" | JellyMusic |

### 9.4 Genre

| Shape | Evidence |
|---|---|
| "play {g} music" / "play {g} songs" | A ("play <genre name> music"), M ("play {Genre} {Music}"), asknavidrome, JellyMusic, kanzi, ours |
| "play some {g} music" / "play some {g}" | M ("play some {Genre} {Music}"), JellyMusic, P ("play some Rock", community bridge), Emby ("put on some music (genre)") |
| "play genre {g}" / "play songs from the genre {g}" | askplex, JellyMusic, kanzi (no official source) |
| partitive forms: "joue du {g}" (kanzi-fr), ours fr "Joue du {genre}" | convergent |

### 9.5 Playlist

| Shape | Evidence |
|---|---|
| "play the playlist {p}" | A (official), P, M, askplex, asknavidrome-adjacent, ours |
| "play the {p} playlist" / "play my {p} playlist" (noun-after) | A (official variations), M ("play my {Playlist} playlist"), asknavidrome, JellyMusic, koko |
| "shuffle the playlist {p}" | P, M, JellyMusic, ours (ShufflePlayIntent) |
| "queue up playlist {p}" / "load playlist {p}" / "play my playlist called {p}" | JellyMusic |

### 9.6 Transport

Official practice (section 3.4): built-in transport intents route Amazon's Common Utterances without custom samples; MSAPI testing guide uses "pause/resume/stop/next/previous/loop/repeat/shuffle" bare. The media-server family ships EMPTY built-ins (askplex, asknavidrome, koko/kanzi, squeeze, ours); MA-prototype attaches samples equal to the official Common Utterances. Our empty built-ins converge; our it-IT StopIntent custom samples ("ferma") sit on the official it-IT Common Utterances ("Ferma"). SEEK shapes official: "fast-forward / rewind <time>", "skip ahead / back <time>", "go forward / back <time>" (ours SkipForwardBackIntent covers skip/jump/go/fast-forward).

### 9.7 Queue / play-next

- "play {s} next" / "queue {s}" / "add {s} to the queue": JellyMusic, ours.
- Album/artist-level queueing ("queue the album {al}", "queue artist {a}"): JellyMusic only. No official source.

### 9.8 Recently added / continue / suggestions

- "what's been recently added?" / "what was recently added?": P, E, ours (QueryRecentlyAddedIntent close variants).
- "play recently added music": M-adjacent (community bridge), koko ("play recently added songs"), ours.
- Continue-watching family: "What was I in the middle of?" / "What movie was I in the middle of?" (P), "what (show/movie/audio book) was I in the middle of?" (E), "what can I keep watching?" (E).
- Suggestions: "Give me a suggestion" / "What's good?" / "Suggest something to watch/listen to" / "Put on some music" (P, E); official-adjacent.
- Liking: "I like this song." / "I don't like this song." are OFFICIAL MSAPI test utterances (ours match).

### 9.9 Radio / station

- "play the station <station>" / "play <station> radio on <skill>" (A official); "Play [artist name] radio" / "Play [library name] library radio" / "Play time travel radio" (P official); "Alexa, play North Pole Radio on iHeartRadio" (store example). The "{artist} radio" bare carrier is an official Plex pattern.

### 9.10 Episode addressing

- Numbered: "play season <#>, episode <#> of <ProgramName>" (A official MSAPI testing guide), "Alexa, watch season 2, episode 4 of Mozart in the Jungle" (A official video testing guide), kanzi all locales, ours.
- BY TITLE: "Play episode You Only Move Twice of The Simpsons" / "Put on episode Swiss Miss of Archer" (P official). We support numbers only.
- Next/latest: "play the new episode of Gotham" / "watch the next episode of ..." (E), "play the latest episode of <programName>" (A), "reproducir el siguiente episodio de {Show}" (kanzi es), ours (PlayNextEpisodeIntent).

## 10. DELTA against our models

Baseline: our es-US template (incl. uncommitted JF-961 rows), en-US template, and the generated it-IT/de-DE/fr-FR models for the key intents (PlaySong, PlayAlbum, PlayArtistSongs, PlayVideo, PlayEpisode, PlayPlaylist, BrowseLibrary, PlayLastAdded, AddToQueue, PlayNext, MediaInfo, Recommend, PlayByGenre, BrowseLibrary fr verified). es-ES spot-checked for PlayEpisode/PlaySong/PlayArtistSongs.

### 10.1 Proven phrasings we LACK

**en family**

1. [A1, STRONGEST] "play songs by {musician}" / "play music by {musician}" / "let's hear songs by {musician}". Official Amazon MSAPI + official Plex + official Emby + My Media official + askplex + asknavidrome + JellyMusic + koko + kanzi. The ecosystem's dominant artist order; we hold only the reversed "play {musician} songs/music" (and upstream held "from"). We hold NEITHER ecosystem order.
2. [A2] "shuffle the album {album}" and "shuffle music by {musician}" / "shuffle songs by {musician}": official Plex ("Shuffle the album Thriller", "Shuffle music by Pink Floyd") + My Media + JellyMusic + kanzi. We route shuffle-artist only through PlayArtistSongs "shuffle {musician}" and have NO album-shuffle intent (session ShuffleOnIntent is not content-scoped).
3. [A3] "play the {playlist} playlist" / "play my {playlist} playlist" (noun-after order): official MSAPI variations + My Media + asknavidrome + JellyMusic + koko. Our PlayPlaylistIntent generates only "{verb} the/my playlist {p}".
4. [A4] "start the album {album}" / "start the record {album}": official Plex ("Start the record Illuminate") + JellyMusic ("start the album"). Our album intent lacks "start".
5. [A5] "play genre {genre}" / "play songs from the genre {genre}": askplex + JellyMusic + kanzi (open-source consensus; no official source carries the noun-first genre form).
6. [A6] "play song {song} from {album}" (album-scoped song): official MSAPI + kanzi en/de/it + My Media it. Feature-adjacent (handler must scope).
7. [A7] "play {artist} radio" (artist-radio carrier): official Plex ("Play [artist name] radio") + official MSAPI station family ("play <station name> radio"). Our PlayRadioIntent requires "radio station {station}". Competition risk with PlayArtistSongs must be probed.
8. [A8] "play artist {musician}" (article-less bare carrier): koko + kanzi + JellyMusic. Minor.
9. [A9] "give me a suggestion" / "what's good?": official Plex + Emby. Our RecommendIntent lacks both.
10. [A10] "what was I in the middle of?" continue-watching family: official Plex + Emby. Our ContinueWatchingIntent/InProgressMediaListIntent have "What was I watching"/"what was i playing" but not the "in the middle of" family.
11. [A11] "what's been recently added?": official Plex/Emby exact form; ours has "what's been added recently" (word-order variant). Marginal.
12. [A12] "queue the album {album}" / "queue artist {musician}": JellyMusic only; needs handler work. R3.
13. [A13] episode-by-TITLE ("Play episode {title} of {series}"): official Plex. Feature gap (we resolve numbers only). R3 feature note, not a sample add.
14. [A14] "make a song request" (iHeartRadio store example): single source, store-prose only. Curiosity; FindSong opener class.

**es family**

1. [E1] "reproduce canciones del artista/cantante/grupo {musician}" / "pon canciones del artista ..." carrier-noun artist rows: askplex es (full grid). Our rows use bare "de {musician}".
2. [E2] "pon a {musician}" (personal-a): askplex es. Single-source; grammatically standard for person-objects; probe for competition with PlayArtistSongs bare rows.
3. [E3] "de quién es esta canción" / "de quién es la canción" / "quién está cantando": askplex es MediaInfo completions. We have "Quién canta esta canción" but not the ownership form.
4. [E4] episode-first with series carrier: "reproduce el episodio {n} de la temporada {m} de la serie {show}" (kanzi es). Our es has the verbless and season-first shapes.
5. [E5] My Media es uses post-wrapper subjunctives ("reproduzca el álbum", "ponga la canción"): convergent with our JF-551 subjunctive wrapper decision. Evidence FOR keeping our subjunctive family; the bare subjunctive imperative forms ("ponga el álbum") are wrapper-internal in MyMedia, not one-shot, so not direct adds.

**it family**

1. [I1] "esegui" as a play verb: koko-it's whole corpus (single source, but a 356-line community corpus). Our verbs: Riproduci/Suona/Metti/Pleia/Ascolta.
2. [I2] My Media it converges with OUR wrapper architecture: "apri My Media di riprodurre l'album" is the same "di + infinitive" shape as our it-IT wrapper family. Also contributes: "dall'album {Album}" (song-from-album scoping), "un po di {Genre} {Music}" (some-genre), "a caso" random/shuffle family ("di riprodurre l'album {Album} a caso"), "di suonare della {Music} {Genre}". The one-shot-able gaps: "un po' di {genre}" and the "a caso" random variants. Our it PlayRandomIntent shapes were not dumped; verify before adding.
3. [I3] "aggiunti da poco / di recente / recentemente" recency synonyms: koko-it. Our it has "novità/ultimi aggiunti".

**de family**

1. [D1] Articled song nouns "spiel das Lied {song}" / "spiel den Song {song}": koko-de + kanzi-de + askplex-de. Ours is article-less "Spiele Lied {song}".
2. [D2] "die CD {album}": koko-de + kanzi-de. Our de has Album/Platte only (it-IT carries cd, de does not).
3. [D3] Artist carrier-noun forms "von dem Künstler {a}" / "von der Band {a}" / "von der Gruppe {a}" (+ Interpreten, feminine forms): kanzi-de + askplex-de. Our de has "Spiele Lieder von {musician}" without carriers. MyMedia de "Spiele {TrackName} von {Artist}" matches ours exactly (convergent on the core shape).
4. [D4] kanzi-de video uses "zeige" (our de uses "Zeig mir"/"schau"): partial coverage, minor.

**fr family**

1. [F1] "par {musician}" artist preposition: kanzi-fr ("joue la chanson {s} par {a}", "joue l'album {al} par {a}"). Our fr uses only "de". No official French music doc reached; single open-source source.
2. [F2] Genre: VERIFIED convergent; our fr already carries "Joue du {genre}", "Joue des chansons {genre}", "Joue de la musique {genre}". Only "joue le genre {genre}" (noun-first) is absent (askplex/JellyMusic/kanzi).
3. [F3] "Joue des chansons de {musician}" indefinite-plural artist carrier: askplex fr. Ours: "Lis les chansons de" definite only.
4. [F4] Browse verbs "affiche/ouvre": kanzi-fr. Our fr browse has "parcourir/montre-moi/lister"; "affiche" bare and "ouvre" absent.

**Cross-cutting**

- Slot architecture: peers use AMAZON.Artist/MusicAlbum built-ins; ours catalog-backed (deliberate, anti-pattern 10 doctrine). Divergence-not-defect, not an alignment candidate.
- Built-in transport empties: converged with the family norm. No change.
- MA prototype's "jugar/giocare" mistranslations and "please"-pairs: noise. Cautionary example against machine-generating locale models.

### 10.2 Ours that are novel-but-sound (keep)

- es-US JF-961 trainer rows, JF-958 genre qualification, es band-name article grids: our live-evidence machinery; peers have nothing comparable. The JF-551 es subjunctive wrappers and it-IT "Di riprodurre" wrappers are independently confirmed by Music Assistant's official per-locale launch wrappers and My Media's es/it command lists.
- it-IT article-agreement grids: independently proven by koko-it's identical grid.
- "Pleia" branding verb: ours alone, device-proven.
- Catalog-backed custom slot types + partial-name synonyms: deliberate architecture.
- Track/song noun duality (en): matches official Plex ("Play the track Come As You Are" / "Play the song Pour It Up") and JellyMusic.
- Liking rows: match official MSAPI test utterances exactly.

### 10.3 Ours that diverge from ecosystem norms (recorded, not auto-changed)

- Artist order: ecosystem "play songs by"; ours "play {musician} songs"; upstream "play songs from". We hold neither ecosystem order.
- de article-less song noun ("Spiele Lied {song}"): peers articulate.
- Album-shuffle: peers treat "shuffle the album X" as a content-scoped intent; ours relies on session-level AMAZON.ShuffleOnIntent.
- MA's mistranslated es/it verbs: cautionary precedent against us ever machine-generating a locale model.

## 11. Recommended alignment list (maintainer approves before any edit)

Ranking: R1 = official Amazon docs or multi-source consensus; R2 = one official vendor source or strong open-source consensus; R3 = single minor source. Every addition must pass the JF-954.2 word-ownership lint and the JF-954.4 graph diff before landing, and genre/artist additions must respect the JF-958 naked-carrier finding (probe before adding any bare two-token carrier).

| # | Rank | Intent | Locale(s) | Candidate sample(s) | Evidence |
|---|------|--------|-----------|---------------------|----------|
| 1 | R1 | PlayArtistSongsIntent | en + en variants | "play songs by {musician}", "play music by {musician}", "let's hear songs by {musician}", "play songs by the artist {musician}" | Amazon MSAPI official + Plex official + Emby official + My Media + askplex + asknavidrome + JellyMusic + koko + kanzi |
| 2 | R1 | PlaySongIntent | de-DE (+de variants) | "Spiele das Lied {song}", "Spiele den Song {song}" (plus "von {musician}" pairs) | koko-de + kanzi-de + askplex-de; MyMedia de confirms core shape |
| 3 | R1 | PlayPlaylistIntent | en (+ mirrors) | "play the {playlist} playlist", "play my {playlist} playlist" (noun-after order) | Amazon MSAPI official variations + My Media + asknavidrome + JellyMusic + koko |
| 4 | R1 | PlayAlbumIntent | en | "start the album {album}", "start the record {album}" | Plex official + JellyMusic |
| 5 | R2 | PlayAlbumIntent | en + mirrors | album-shuffle rows "shuffle the album {album}" (needs content-scoped shuffle decision: new intent or extend ShufflePlayIntent) | Plex official + My Media + JellyMusic |
| 6 | R2 | PlayArtistSongsIntent | es family | "Reproduce canciones del artista {musician}", "Pon canciones del artista {musician}", plus "del cantante/grupo" variants | askplex es carrier grid |
| 7 | R2 | PlayByGenreIntent | en | "play genre {genre}", "play songs from the genre {genre}" | askplex + JellyMusic + kanzi (open-source consensus) |
| 8 | R2 | PlayArtistSongsIntent | de-DE | "Spiele Lieder von der Band {musician}", "Spiele Musik von dem Künstler {musician}" (plus Gruppe) | kanzi-de + askplex-de |
| 9 | R2 | PlaySongIntent | it-IT | "un po' di {genre}" genre rows in PlayByGenre it + "a caso" random variants (verify our it PlayRandomIntent shapes first) | My Media it official |
| 10 | R2 | PlaySongIntent | fr-FR | "Joue la chanson {song} par {musician}" (plus album "par" pair) | kanzi-fr (single source; "par" is standard French usage) |
| 11 | R2 | PlayRadioIntent | en | artist-radio carrier, e.g. "play {musician} radio" (PROBE FIRST: competition with PlayArtistSongs) | Plex official + MSAPI station family |
| 12 | R2 | RecommendIntent | en | "give me a suggestion", "what's good?" | Plex official + Emby official |
| 13 | R2 | ContinueWatchingIntent / InProgressMediaListIntent | en | "what was I in the middle of?", "what movie was I in the middle of?" | Plex official + Emby official |
| 14 | R2 | PlayAlbumIntent | de-DE | "Spiele die CD {album}" | koko-de + kanzi-de |
| 15 | R2 | PlayArtistSongsIntent | it-IT | "Riproduci la band {musician}", "Riproduci l'artista {musician}" carrier forms | askplex it |
| 16 | R2 | PlayArtistSongsIntent | fr-FR | "Joue le groupe {musician}", "Joue des chansons de l'artiste {musician}" | askplex fr |
| 17 | R3 | PlaySongIntent | it-IT | "esegui" verb rows ("Esegui {song}", "Esegui la canzone {song}") | koko-it only; probe NLU competition with our five verbs first |
| 18 | R3 | MediaInfoIntent | en | "who is this song by", "what is the name of the song that is playing" | askplex en |
| 19 | R3 | MediaInfoIntent | es family | "de quién es esta canción", "quién está cantando" | askplex es |
| 20 | R3 | PlayLastAddedIntent | it-IT | "riproduci canzoni aggiunte di recente", "aggiunte da poco" family | koko-it |
| 21 | R3 | BrowseLibraryIntent | en | "open {browse_category}", "go to {browse_category}" | kanzi en |
| 22 | R3 | PlayEpisodeIntent | es family | "reproduce el episodio {n} de la temporada {m} de la serie {show}" | kanzi es |
| 23 | R3 | PlayArtistSongsIntent | en | "play artist {musician}" (article-less) | koko + kanzi + JellyMusic |
| 24 | R3 | PlayArtistSongsIntent | es family | "pon a {musician}" | askplex es; probe competition |
| 25 | R3 | FEATURE notes (not sample adds) | en | episode-by-title (Plex official); album/artist-level queueing (JellyMusic); "play song {song} from {album}" scoping (MSAPI official + kanzi + MyMedia it) | need handler work before any model rows |

Not recommended: verbless noun carriers ("Song {s} by {a}", askplex), collision with the JF-958 greedy-carrier finding; MA's "jugar/giocare" and "please" pairs; custom samples on built-in transport intents (contradicts our anti-pattern 5 doctrine; official Common Utterances already route); koko's "Mucke" (regional slang); Emby's device-navigation grammar (different device class); iHeartRadio's "make a song request" (single store-prose source, intent-shaped like our FindSong openers which already exist).

## 12. Verification notes and caveats

- Section 8 samples were extracted mechanically from fetched JSON/txt. askplex es-US/es-ES byte-identity diff-verified. MA paths corrected from `main` to `master` after a 404.
- Section 2 store content comes from Wayback snapshots of retail pages (snapshot IDs cited inline); live pages are bot-walled. Snapshot eras differ (Plex 2026-03, iHeartRadio 2021-12), so phrase-carousel presence is not comparable across skills.
- Section 3.4 locale labels follow the doc's table order and were not re-verified per header cell; treat as high-confidence but page-order-derived.
- The "Plex disabled the official skill on June 15, 2026" claim is third-party and UNCONFIRMED.
- One memory-origin article id was discarded during extraction (support.plex.tv 206120658 shown dead/nonexistent).
- Our baselines read from generated model JSONs (build output); the es-US baseline reflects the working tree's uncommitted JF-961 rows. it-IT PlayRandomIntent shapes were not dumped (item 9 flags the verify-first step).
- Locale coverage achieved for the delta: en, es, it, de, fr. The remaining 12 locales were not baselined against peers (no peer models exist for most; ja/ar/hi peers are absent from every corpus mined).
