using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Alexa intent name constants.
/// </summary>
internal static class IntentNames
{
    public const string MarkFavorite = "MarkFavoriteIntent";
    public const string UnmarkFavorite = "UnmarkFavoriteIntent";
    public const string RateItem = "RateItemIntent";
    public const string MediaInfo = "MediaInfoIntent";
    public const string PlayFavorites = "PlayFavoritesIntent";
    public const string PlayAlbum = "PlayAlbumIntent";
    public const string PlayArtistSongs = "PlayArtistSongsIntent";
    public const string PlayChannel = "PlayChannelIntent";
    public const string Play = "PlayIntent";
    public const string PlayLastAdded = "PlayLastAddedIntent";
    public const string PlayPlaylist = "PlayPlaylistIntent";
    public const string AddCurrentToPlaylist = "AddCurrentToPlaylistIntent";
    public const string AddSongToPlaylist = "AddSongToPlaylistIntent";
    public const string RemoveCurrentFromPlaylist = "RemoveCurrentFromPlaylistIntent";
    public const string CreatePlaylist = "CreatePlaylistIntent";
    public const string ShufflePlay = "ShufflePlayIntent";
    public const string PlaySong = "PlaySongIntent";
    public const string PlayVideo = "PlayVideoIntent";

    /// <summary>
    /// JF-874: the it-IT catalog-backed wrapper-hosting twin of
    /// <see cref="PlayVideo"/>. Its <c>movie</c> slot is the MovieTitle catalog
    /// type, which is what makes the one-shot wrapper tails
    /// ("chiedi a mia collezione di mettere il film X") selectable (the
    /// SearchQuery title span is wrapper-hostile; scratch A/B 2026-10-12).
    /// Served by <see cref="Handler.PlayVideoIntentHandler"/>, whose logic is
    /// shared verbatim.
    /// </summary>
    public const string PlayMovie = "PlayMovieIntent";
    public const string PlayRandom = "PlayRandomIntent";
    public const string PlayByGenre = "PlayByGenreIntent";
    public const string PlayByDecade = "PlayByDecadeIntent";
    public const string PlayMoodMusic = "PlayMoodMusicIntent";
    public const string ContinueWatching = "ContinueWatchingIntent";
    public const string PlayBook = "PlayBookIntent";
    public const string GoToChapter = "GoToChapterIntent";
    public const string InProgressMediaList = "InProgressMediaListIntent";
    public const string BrowseLibrary = "BrowseLibraryIntent";
    public const string Recommend = "RecommendIntent";
    public const string SleepTimer = "SleepTimerIntent";
    public const string PlayEpisode = "PlayEpisodeIntent";
    public const string PlayNextEpisode = "PlayNextEpisodeIntent";

    // JF-450 loop-mode vocabulary: de-DE, fr-FR, fr-CA and it-IT declare the custom
    // loop intents (locale vocabulary: "Ripeti la canzone", "Répète la chanson",
    // "Lied wiederholen"); since JF-562 they ALSO declare the AMAZON.LoopOn/LoopOff
    // built-ins like every other locale. The OFF pair still dual-accepts both
    // names (do not remove it: Amazon-side models built before the JF-562
    // declarations still deliver only the custom names, and the built-ins can
    // win NLU in these four locales); the ON pair's dual accept ended at JF-901,
    // do not re-add it. Since JF-901
    // the bare built-in AMAZON.LoopOnIntent ("loop", "attiva il loop"; Amazon's
    // own loop-THIS-track convention, live device evidence 2026-10-17: corr
    // 6883f581 resolved the phrase to the built-in while the old repeat-all
    // mapping advanced the queue at track end, corr 4e6ce000) applies
    // repeat-ONE, so LoopSongOnIntentHandler owns it beside its custom twins;
    // LoopAllOnIntent keeps repeat-all in LoopOnIntentHandler, and
    // LoopAllOffIntent stays paired with the AMAZON.LoopOffIntent built-in
    // (repeat-none). Since JF-902 the model layer matches that split in the
    // four custom locales: the BARE loop phrases ("Attiva loop", "attiva il
    // loop", bare "loop", "Schleife an", "Active la boucle", ...) are exact
    // rows on LoopSongOnIntent, and LoopAllOnIntent carries only
    // queue-scoped carriers ("Ripeti la coda", "Wiederhole die Warteschlange",
    // "Répète la file d'attente"), so no utterance maps to both modes anymore
    // (the rows were moved, not dropped: without exact rows the phrases stole
    // live; see the template comments).
    // REMAINING GAP (JF-904): LoopAllOnIntent is still declared in only those
    // four locales, so the other 13 have NO voice path to repeat-all at all
    // (the bare built-in is repeat-one now); restoring a queue-loop carrier
    // family across the 13 built-in-only locales is its own tracked task.
    public const string LoopSongOn = "LoopSongOnIntent";
    public const string RepeatSingleOn = "RepeatSingleOnIntent";
    public const string LoopAllOn = "LoopAllOnIntent";
    public const string LoopAllOff = "LoopAllOffIntent";

    // ShuffleAllOn/OffIntent pair with the AMAZON.ShuffleOn/OffIntent built-ins the
    // same way the loop family's OFF pair still does (live incident 2026-09-23:
    // «mescola la coda» statistically landed on ClearQueueIntent because the it-IT
    // built-in carries no custom samples and anti-pattern #5 forbids adding them;
    // custom twins are the loop-family precedent; JF-901 split the loop ON pair
    // across two handlers). Handlers accept BOTH names per mode.
    public const string ShuffleAllOn = "ShuffleAllOnIntent";
    public const string ShuffleAllOff = "ShuffleAllOffIntent";
    public const string AddToQueue = "AddToQueueIntent";
    public const string PlayNext = "PlayNextIntent";
    public const string ClearQueue = "ClearQueueIntent";
    public const string ListQueue = "ListQueueIntent";
    public const string PlayRadio = "PlayRadioIntent";
    public const string TurnRadioOn = "TurnRadioOnIntent";
    public const string TurnRadioOff = "TurnRadioOffIntent";
    public const string LearnMyVoice = "LearnMyVoiceIntent";
    public const string WhoAmI = "WhoAmIIntent";
    public const string QueryArtistLibrary = "QueryArtistLibraryIntent";
    public const string PlayPodcast = "PlayPodcastIntent";
    public const string SearchMedia = "SearchMediaIntent";
    public const string SetReminder = "SetReminderIntent";
    public const string QueryRecentlyAdded = "QueryRecentlyAddedIntent";
    public const string FollowMe = "FollowMeIntent";
    public const string SkipForwardBack = "SkipForwardBackIntent";
    public const string JumpToPosition = "JumpToPositionIntent";
    public const string ShowMore = "ShowMoreIntent";
    public const string FindSongIntent = "FindSongIntent";
    public const string FindSongByArtistIntent = "FindSongByArtistIntent";
    public const string SetPlaybackSpeed = "SetPlaybackSpeedIntent";
    public const string ShowPhotoAlbum = "ShowPhotoAlbumIntent";

    public const string AmazonFallback = "AMAZON.FallbackIntent";
    public const string AmazonLoopOff = "AMAZON.LoopOffIntent";
    public const string AmazonLoopOn = "AMAZON.LoopOnIntent";
    public const string AmazonNext = "AMAZON.NextIntent";
    public const string AmazonPause = "AMAZON.PauseIntent";
    public const string AmazonStop = "AMAZON.StopIntent";
    public const string AmazonCancel = "AMAZON.CancelIntent";
    public const string AmazonPrevious = "AMAZON.PreviousIntent";
    public const string AmazonResume = "AMAZON.ResumeIntent";
    public const string AmazonShuffleOff = "AMAZON.ShuffleOffIntent";
    public const string AmazonShuffleOn = "AMAZON.ShuffleOnIntent";
    public const string AmazonStartOver = "AMAZON.StartOverIntent";
    public const string AmazonRepeat = "AMAZON.RepeatIntent";
    public const string AmazonYes = "AMAZON.YesIntent";
    public const string AmazonNo = "AMAZON.NoIntent";

    /// <summary>
    /// Request type for the proactive events subscription changed callback.
    /// </summary>
    public const string ProactiveSubscriptionChanged = "AlexaSkillEvent.ProactiveSubscriptionChanged";

    /// <summary>
    /// Every intent-name string constant declared on this class, custom and AMAZON
    /// built-ins alike, excluding <see cref="ProactiveSubscriptionChanged"/> (a
    /// request type, not an intent name). Reflection-derived so a derived list
    /// cannot drift from the constants (the simulator's hand-maintained copy had
    /// already lost FindSongIntent, JF-456).
    /// </summary>
    internal static IReadOnlyList<string> AllIntentNames { get; } =
        typeof(IntentNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f.FieldType == typeof(string) && f.Name != nameof(ProactiveSubscriptionChanged))
            .Select(f => (string?)f.GetValue(null))
            .OfType<string>()
            .ToList();

    /// <summary>
    /// The custom (non-AMAZON.*) intent names only: the handler-owned vocabulary.
    /// </summary>
    internal static IReadOnlyList<string> AllCustomIntentNames { get; } =
        AllIntentNames
            .Where(n => !n.StartsWith("AMAZON.", StringComparison.Ordinal))
            .ToList();

    /// <summary>Alexa slot name constants used across handlers.</summary>
    public static class Slots
    {
        public const string TitleKeywords = "titleKeywords";
        public const string Musician = "musician";
        public const string Album = "album";
        public const string Song = "song";
        // JF-614: the AddSong dialog slot is a pure-text SearchQuery (the
        // MusicRecording ER slot does not fill from free-text titles during
        // dialog management), so it needs its own NAME to keep the one-type-
        // per-name rule with PlaySongIntent's MusicRecording "song".
        public const string SongQuery = "song_query";
        public const string Station = "station";

        /// <summary>JF-636: the SpeedRate slot on SetPlaybackSpeedIntent.</summary>
        public const string Speed = "speed";

        /// <summary>
        /// Playlist-name slot on AddSongToPlaylistIntent. Named differently from
        /// <see cref="Playlist"/> because that name is typed AMAZON.SearchQuery in
        /// PlayPlaylist/ShufflePlayIntent, and a slot name must keep one type across
        /// which SearchQuery forbids (CLAUDE.md anti-pattern #2), so it is free-text
        /// AMAZON.MusicRecording like the album slot on the 16 free-text locales.
        /// </summary>
        public const string PlaylistTarget = "playlist_target";
        public const string Playlist = "playlist";

        /// <summary>
        /// Star-count slot on RateItemIntent: AMAZON.NUMBER in the 16 digit
        /// locales, ItalianNumber in it-IT (whose ASR delivers "cinque"), parsed
        /// by ItalianNumberWords on both shapes.
        /// </summary>
        public const string StarRating = "star_rating";

        /// <summary>
        /// JF-874: the MovieTitle catalog-typed slot on PlayMovieIntent. Its own
        /// NAME (not "title") because PlayVideoIntent's title is
        /// AMAZON.SearchQuery and a slot name keeps one type per locale
        /// (the JF-614 song/song_query precedent).
        /// </summary>
        public const string Movie = "movie";
    }
}
