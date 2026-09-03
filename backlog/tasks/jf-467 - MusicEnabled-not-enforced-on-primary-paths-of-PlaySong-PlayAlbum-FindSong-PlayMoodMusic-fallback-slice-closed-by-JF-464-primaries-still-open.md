---
id: JF-467
title: >-
  MusicEnabled not enforced on primary paths of
  PlaySong/PlayAlbum/FindSong/PlayMoodMusic (fallback slice closed by JF-464,
  primaries still open)
status: To Do
assignee: []
created_date: '2026-09-03 08:38'
updated_date: '2026-09-03 09:03'
labels: []
dependencies: []
references:
  - JF-464 review finding B
  - JF-464 (the fallback-slice gate)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ (PlaySong, PlayAlbum,
    FindSong, PlayMoodMusic)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-464 review pass (2026-09-03, finding B, score 82). MusicEnabled is enforced only on SOME paths: PlayRandom, SearchMedia, Resume, ContinueWatching, Recommend, StartOver, BrowseLibrary, PlayByGenre's genre query go through FilterByContentAccess, but the PRIMARY paths of the core music intents hard-code Audio/MusicAlbum/MusicArtist types and never consult the flag: PlaySongIntentHandler (~:236), PlayAlbumIntentHandler (~:490), FindSongIntentHandler (~:533), PlayMoodMusicIntentHandler mood-hit path (~:437). IfMediaTypeDisabled has ZERO production callers. With MusicEnabled=false a user can still say "play bohemian rhapsody" / "play the album dark side of the moon" / "play chill music" and get music playback. JF-464 closed only the fallback slice (TryEntityFallbackAsync returns null when music is off).

Design decision needed first (this is a behavior change, not a mechanical fix): what SHOULD a music-disabled user hear on a direct music request? Options: (a) the generic MediaTypeNotAvailable-style response used elsewhere for disabled types (check the existing ResponseStrings keys and how video/book-disabled paths respond today); (b) a plain not-found. Match whatever convention the already-gated paths use so the skill speaks one consistent message. Then add the gate at each primary entry via the SAME mechanism (IfFeatureDisabled or FilterByContentAccess result check, whichever the already-gated paths use; do not invent a third pattern).

Note the flag is global-only (no per-user override exists, verified 2026-09-03); do not invent one as part of this task.

Acceptance criteria:
- Music-disabled + "play <song>" / "play album X" / FindSong / mood-hit: the chosen disabled-type response, no AudioPlayer directive, no library query issued.
- Music-enabled: all four paths byte-identical behavior to today (existing tests untouched and green).
- One test per gated entry, following CrossMediaFallbackMusicGateTests conventions.
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
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REVIEW LANDING (JF-464 final code-review, P3@80): the new music gates read the handler-injected _config while FilterByContentAccess/IfMediaTypeDisabled read Plugin.Instance.Configuration. The two diverge when the STANDARD Jellyfin updatePluginConfiguration endpoint replaces the Configuration object (handlers are DI singletons capturing _config once). While implementing this task, align the read source: either read Plugin.Instance?.Configuration in the gates (null-tolerant, mirroring IfMediaTypeDisabled) or verify Jellyfin's config save path actually replaces the object for this plugin and document why _config is safe. Do not leave the two conventions mixed on the same flag.

EXECUTION NOTE (JF-464 review): the mood handler's own SearchByArtistGenreAsync tier (PlayMoodMusicIntentHandler ~:457) still plays music ungated when genre tracks hit. This task's 'no library query issued' acceptance criterion means each of the four handlers must be gated at ENTRY, before any query, not only the specific lines enumerated in the description.
<!-- SECTION:NOTES:END -->
