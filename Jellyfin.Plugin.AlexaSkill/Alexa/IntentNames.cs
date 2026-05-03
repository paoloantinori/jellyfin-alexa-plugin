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
    public const string PlaySong = "PlaySongIntent";
    public const string PlayVideo = "PlayVideoIntent";
    public const string PlayRandom = "PlayRandomIntent";
    public const string PlayByGenre = "PlayByGenreIntent";
    public const string ContinueWatching = "ContinueWatchingIntent";
    public const string GoToChapter = "GoToChapterIntent";
    public const string LoopSongOn = "LoopSongOnIntent";

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
}
