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
    public const string MediaInfo = "MediaInfoIntent";
    public const string PlayFavorites = "PlayFavoritesIntent";
    public const string PlayAlbum = "PlayAlbumIntent";
    public const string PlayArtistSongs = "PlayArtistSongsIntent";
    public const string PlayChannel = "PlayChannelIntent";
    public const string Play = "PlayIntent";
    public const string PlayLastAdded = "PlayLastAddedIntent";
    public const string PlayPlaylist = "PlayPlaylistIntent";
    public const string ShufflePlay = "ShufflePlayIntent";
    public const string PlaySong = "PlaySongIntent";
    public const string PlayVideo = "PlayVideoIntent";
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
    // loop intents instead of the AMAZON.LoopOn/LoopOff built-ins the other locales
    // use (locale vocabulary: "Ripeti la canzone", "Répète la chanson", "Lied
    // wiederholen"), so the loop handlers' CanHandle accepts BOTH names per mode:
    // LoopAllOnIntent pairs with AMAZON.LoopOnIntent (repeat-all),
    // LoopAllOffIntent with AMAZON.LoopOffIntent (repeat-none), and
    // RepeatSingleOnIntent is the repeat-one sibling of LoopSongOnIntent.
    public const string LoopSongOn = "LoopSongOnIntent";
    public const string RepeatSingleOn = "RepeatSingleOnIntent";
    public const string LoopAllOn = "LoopAllOnIntent";
    public const string LoopAllOff = "LoopAllOffIntent";
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
        public const string Station = "station";
    }
}
