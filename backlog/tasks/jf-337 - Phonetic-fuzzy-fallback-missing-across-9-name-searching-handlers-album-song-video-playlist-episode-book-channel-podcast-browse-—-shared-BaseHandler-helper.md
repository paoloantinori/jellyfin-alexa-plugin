---
id: JF-337
title: >-
  Phonetic/fuzzy fallback missing across 9 name-searching handlers
  (album/song/video/playlist/episode/book/channel/podcast/browse) — shared
  BaseHandler helper
status: To Do
assignee: []
created_date: '2026-07-13 05:55'
updated_date: '2026-07-13 20:16'
labels:
  - search
  - phonetic
  - handler
  - asr
  - i18n
  - tech-debt
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayVideoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayChannelIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/BrowseLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Umbrella for the systematic gap found by a handler sweep (2026-07-13). JF-336 (PlayAlbum) is the first concrete instance; this task covers the OTHER 9 name-searching handlers with the same gap + the shared fix.

Verified by handler survey: only 2 of ~12 name-searching handlers have a phonetic/fuzzy fallback — PlayArtistSongsIntentHandler (4-tier ArtistSearch with Double Metaphone) and FindSongIntentHandler (SongNgramIndexService: n-gram + phonetic + DB). The other 10 do an EXACT Jellyfin searchTerm query for the user-spoken name and have no phonetic fallback, so ASR transcription/accent/spelling variants fail (the JF-336 "caffè" vs "Cafe" pattern):

GAP handlers (exact searchTerm, no phonetic fallback):
- PlayAlbumIntentHandler  → JF-336 (first instance)
- PlaySongIntentHandler (uses SearchWithAsrFallbackAsync — only compound-word variants, still exact search)
- PlayVideoIntentHandler (exact title search → NotFoundVideo)
- SearchMediaIntentHandler (SearchWithAsrFallbackAsync, unified content types → MediaNotFound)
- PlayPlaylistIntentHandler (exact, FuzzyMatch only for disambiguation → NotFoundPlaylist)
- PlayEpisodeIntentHandler (exact series name → NotFoundSeries)
- PlayBookIntentHandler (exact, FuzzyMatch only for disambiguation → NotFoundBook)
- PlayChannelIntentHandler (exact channel name → NotFoundChannel)
- PlayPodcastIntentHandler (exact podcast name → NotFoundPodcast)
- BrowseLibraryIntentHandler (exact filter when provided → NoBrowseResults)

Right-altitude fix: a shared BaseHandler phonetic-fallback helper (reuse FuzzyMatcher / Double Metaphone — same primitive ArtistSearch and SongNgramIndex already use), adopted by each handler. Avoids 10 ad-hoc reimplementations.

Cross-language: handlers are locale-agnostic (one C# path serves all 17 locales), so the fix applies to every language automatically. Double Metaphone is acceptable for Romance locales (it-IT/es/fr/pt/de per the survey) but weak for non-Romance (ja-JP/ar-SA/hi-IN); locale-aware phonetics (cf. PhoneticSynonymGenerator) is a possible follow-up. The fix does NOT depend on the catalog-backed slot (JF-335) — it operates on the free-text slot value.

Related: JF-336 (PlayAlbum, first instance + artist-fallback-threshold sub-issue), JF-335 (catalog sync multilingual — complementary, improves slot-fill confidence).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Add a shared helper in BaseHandler (e.g. SearchItemsPhoneticAsync(query, itemTypes, user)) that, when the exact Jellyfin searchTerm query returns 0, fetches candidate items and phonetic/fuzzy-matches names via the existing FuzzyMatcher (Double Metaphone) — reusing the same primitive PlayArtistSongs (ArtistSearch) and FindSong (SongNgramIndexService) already use. Keep the miss path cheap (cold path only).
- [ ] #2 Adopt the helper in the 9 name-searching handlers that currently do exact-only search: PlaySong, PlayVideo, SearchMedia, PlayPlaylist, PlayEpisode, PlayBook, PlayChannel, PlayPodcast, BrowseLibrary. (PlayAlbum is JF-336 — do it first as the reference adoption, then generalize.)
- [ ] #3 Each adoption: on exact-search 0 results, try the phonetic fallback BEFORE the current 'not found' / wrong cross-media fallback. Verify one representative repro per media type via the Jellyfin simulator + logs (e.g. accented/transcribed title resolves to the library item).
- [ ] #4 Locale dimension: the helper is locale-agnostic (one C# path, all 17 locales). Verify Double Metaphone quality is acceptable for Romance locales (it-IT/es-ES/fr-FR/pt-BR/de-DE); document the known weakness for non-Romance locales (ja-JP/ar-SA/hi-IN) and whether locale-aware phonetics (cf. PhoneticSynonymGenerator) are needed as a follow-up.
- [ ] #5 No regression: exact-name playback unchanged for all 9 handlers; PlayArtistSongs/FindSong untouched; run the existing NLU/E2E fixtures.
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
