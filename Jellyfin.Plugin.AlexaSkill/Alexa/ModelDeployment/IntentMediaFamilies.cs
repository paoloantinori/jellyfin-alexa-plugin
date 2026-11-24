using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// The media families the JF-960 media-profiled model pruning distinguishes. A family
/// is PRESENT on an instance when the census (<see cref="MediaCensus"/>) finds any of
/// its kinds reachable by at least one linked user.
/// </summary>
public enum MediaFamily
{
    /// <summary>Movies, TV series, episodes (Movie, Series, Episode).</summary>
    Video,

    /// <summary>Music playback (Audio, MusicAlbum, MusicArtist).</summary>
    Music,

    /// <summary>Audiobooks (AudioBook).</summary>
    Audiobook,

    /// <summary>Podcasts, both Jellyfin storage shapes (JF-599: MusicAlbum-of-Audio and Series-of-Episodes).</summary>
    Podcast,

    /// <summary>Live tuner channels (LiveTvChannel), not library items.</summary>
    LiveTv
}

/// <summary>
/// THE intent-to-media-family pruning table (JF-960, single definition): every row
/// maps a custom intent whose SOLE purpose is one media family to that family. When
/// <see cref="Configuration.PluginConfiguration.MediaProfiledModels"/> is on, the
/// rebuild drops every tabled intent whose family census is zero; every intent NOT in
/// the table is media-neutral and is NEVER pruned. Under-pruning is safe, over-pruning
/// breaks users, so a row is admitted only when the serving handler's queries are
/// entirely inside one family (each row cites the handler evidence); dual-media
/// intents stay out and are enumerated in the exclusions comment below.
/// </summary>
public static class IntentMediaFamilies
{
    /// <summary>
    /// The pruning table. Keys are intent names exactly as the locale models declare
    /// them and the handlers match them (<see cref="Alexa.IntentNames"/> constants);
    /// values are the family whose absence prunes the intent. AMAZON built-ins and
    /// fallback intents are deliberately unreachable from this table.
    /// </summary>
    public static IReadOnlyDictionary<string, MediaFamily> Table { get; } = new Dictionary<string, MediaFamily>(StringComparer.Ordinal)
    {
        // ---- VIDEO: PlayVideoIntentHandler queries Episode + Movie only. PlayMovie is
        // the JF-874 catalog-backed twin served in place by the same handler. PlayEpisode
        // queries Episode only. PlayNextEpisode resolves TV next-up episodes only
        // (TvNextUpService).
        [Alexa.IntentNames.PlayVideo] = MediaFamily.Video,
        [Alexa.IntentNames.PlayMovie] = MediaFamily.Video,
        [Alexa.IntentNames.PlayEpisode] = MediaFamily.Video,
        [Alexa.IntentNames.PlayNextEpisode] = MediaFamily.Video,

        // ---- MUSIC: every row's handler queries Audio / MusicAlbum / MusicArtist only
        // (handler survey: PlaySong, PlayAlbum, PlayArtistSongs, PlayMoodMusic,
        // PlayByGenre, PlayByDecade, FindSong chain, AddToQueue, PlayNext,
        // QueryArtistLibrary). PlayLibrarySong is the JF-940 carrier-noun twin served in
        // place by PlaySongIntentHandler; FindSongByArtist is served by
        // FindSongIntentHandler on the same Audio-only chain.
        [Alexa.IntentNames.PlaySong] = MediaFamily.Music,
        [Alexa.IntentNames.PlayLibrarySong] = MediaFamily.Music,
        [Alexa.IntentNames.PlayAlbum] = MediaFamily.Music,
        [Alexa.IntentNames.PlayArtistSongs] = MediaFamily.Music,
        [Alexa.IntentNames.PlayMoodMusic] = MediaFamily.Music,
        [Alexa.IntentNames.PlayByGenre] = MediaFamily.Music,
        [Alexa.IntentNames.PlayByDecade] = MediaFamily.Music,
        [Alexa.IntentNames.FindSongIntent] = MediaFamily.Music,
        [Alexa.IntentNames.FindSongByArtistIntent] = MediaFamily.Music,
        [Alexa.IntentNames.AddToQueue] = MediaFamily.Music,
        [Alexa.IntentNames.PlayNext] = MediaFamily.Music,
        [Alexa.IntentNames.QueryArtistLibrary] = MediaFamily.Music,

        // ---- AUDIOBOOK: PlayBookIntentHandler queries AudioBook only.
        [Alexa.IntentNames.PlayBook] = MediaFamily.Audiobook,

        // ---- PODCAST: PlayPodcastIntentHandler queries MusicAlbum + Series (the two
        // JF-599 storage shapes; JF-640 runs both unconditionally). The shapes have no
        // type-level discriminator, so the census detects the family through the same
        // container kinds the handler itself queries.
        [Alexa.IntentNames.PlayPodcast] = MediaFamily.Podcast,

        // ---- LIVE_TV: PlayChannelIntentHandler queries LiveTvChannel only.
        [Alexa.IntentNames.PlayChannel] = MediaFamily.LiveTv,
    };

    // Deliberately NOT in the table (media-neutral, never pruned), each with the reason:
    // - PlayRadioIntent: DUAL media. The context-seeded path derives radio tracks from
    //   Audio (Radio.FindRadioTracksAsync) while the named-station tier queries
    //   LiveTvChannel with MediaTypes=Audio (JF-474). The task proposal's music-only
    //   reading does not match the handler; dual-media intents are never pruned.
    // - PlayRandomIntent, RecommendIntent, BrowseLibraryIntent, SearchMediaIntent:
    //   multi-family by design (media_type slot / cross-family query unions:
    //   PlayRandom+Recommend+ContinueWatching+InProgressMediaList+QueryRecentlyAdded
    //   each query Audio+Movie+Episode unions; BrowseLibrary and SearchMedia span
    //   music+video+audiobook).
    // - MediaInfoIntent, ResumeIntent, StartOverIntent: resolve whatever is playing or
    //   was last played, any family.
    // - GoToChapterIntent: navigates chapters of the CURRENT item, any family with
    //   chapters (audiobooks and videos alike).
    // - ShowLyricsIntent: bound to the currently playing track.
    // - ShowPhotoAlbumIntent: the photo family, which this slice does not prune.
    // - Playlist family (PlayPlaylist, ShufflePlay, CreatePlaylist, AddSongToPlaylist,
    //   AddCurrentToPlaylist, RemoveCurrentFromPlaylist): playlist workflows are
    //   cross-type (playlists are always allowed; AddSongToPlaylist queries Audio today,
    //   but the workflow surface is cross-type, so it stays out pending a dedicated
    //   slice).
    // - Favorites/recently-added/browse aggregates, transport and session intents
    //   (pause/resume/next/previous/loop/shuffle/queue/sleep timer/speed/reminders/
    //   voice identity), AMAZON.* built-ins, and FallbackIntent: media-neutral.
    //
    // A future row must cite its handler's full query surface in a comment here; a row
    // whose handler ever queries two families must NOT be added (the table is
    // single-family by construction).

    /// <summary>
    /// Looks up the family for an intent name, or null when the intent is not in the
    /// table (media-neutral).
    /// </summary>
    /// <param name="intentName">The intent name from the model.</param>
    /// <returns>The family, or null when the intent is media-neutral.</returns>
    public static MediaFamily? FamilyOf(string intentName)
    {
        return Table.TryGetValue(intentName, out MediaFamily family) ? family : null;
    }

    /// <summary>
    /// The census kinds per family, shared by <see cref="MediaCensus"/> so the family
    /// definition lives next to the table (single definition).
    /// PODCAST kinds are the container kinds PlayPodcastIntentHandler queries: the two
    /// storage shapes (MusicAlbum-of-Audio, Series-of-Episodes, JF-599) have no
    /// type-level discriminator, so the census uses the same kinds the handler serves.
    /// That deliberately UNDER-detects podcast absence: any music album or TV series
    /// keeps the family present, so PlayPodcastIntent is only pruned when the handler's
    /// own two shape queries could return nothing.
    /// </summary>
    internal static IReadOnlyDictionary<MediaFamily, Jellyfin.Data.Enums.BaseItemKind[]> FamilyKinds { get; } =
        new Dictionary<MediaFamily, Jellyfin.Data.Enums.BaseItemKind[]>
        {
            [MediaFamily.Video] = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Movie,
                Jellyfin.Data.Enums.BaseItemKind.Series,
                Jellyfin.Data.Enums.BaseItemKind.Episode
            },
            [MediaFamily.Music] = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.Audio,
                Jellyfin.Data.Enums.BaseItemKind.MusicAlbum,
                Jellyfin.Data.Enums.BaseItemKind.MusicArtist
            },
            [MediaFamily.Audiobook] = new[] { Jellyfin.Data.Enums.BaseItemKind.AudioBook },
            [MediaFamily.Podcast] = new[]
            {
                Jellyfin.Data.Enums.BaseItemKind.MusicAlbum,
                Jellyfin.Data.Enums.BaseItemKind.Series
            },
            [MediaFamily.LiveTv] = new[] { Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel },
        };

    /// <summary>
    /// Stable display name for logs (the enum ToString would do, but the lowercase
    /// family names read better in the per-locale prune log line).
    /// </summary>
    /// <param name="family">The family.</param>
    /// <returns>The display name.</returns>
    public static string DisplayName(MediaFamily family)
    {
        return family switch
        {
            MediaFamily.Video => "video",
            MediaFamily.Music => "music",
            MediaFamily.Audiobook => "audiobook",
            MediaFamily.Podcast => "podcast",
            MediaFamily.LiveTv => "live_tv",
            _ => family.ToString()
        };
    }
}
