using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaySongIntent requests.
/// </summary>
public class PlaySongIntentHandler : BaseHandler
{
    private static readonly string[] SongCarrierPhrases = new[]
    {
        // Phrases that appear right before {song} in utterance templates.
        // Within each locale group, longer phrases come first (e.g. "the song called " before "the song ").
        // English
        "the song called ", "a song called ",
        "that song ", "the song ", "the track ", "a song ", "a track ",
        // Italian
        "la canzone ", "il brano ", "il pezzo ", "la traccia ",
        "una canzone ", "un brano ", "un pezzo ", "una traccia ",
        "canzone ", "brano ", "pezzo ", "traccia ",
        // German
        "das lied ", "das stück ", "den titel ",
        "ein lied ", "ein stück ",
        // Spanish
        "la canción ", "el tema ", "una canción ", "canción ",
        // French
        "la chanson ", "le titre ", "le morceau ", "une chanson ", "chanson ",
        // Dutch
        "het liedje ", "het nummer ", "liedje ", "nummer ",
        // Portuguese
        "a música ", "a faixa ", "música ",
    };

    // Generic words meaning "music/songs" across supported locales.
    // When Alexa captures one of these as the {song} slot alongside a {musician} slot,
    // the user means "play music by <artist>" not "play a song titled 'music'".
    private static readonly HashSet<string> GenericMusicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "music", "songs",
        // Italian
        "musica", "canzoni", "brani",
        // German
        "musik", "lieder",
        // Spanish
        "música", "canciones",
        // French
        "chansons", "musique",
        // Dutch
        "muziek", "liedjes",
        // Portuguese
        "canções",
    };

    private ILibraryManager _libraryManager;
    private IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySongIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlaySongIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlaySong, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a specific song by name, optionally filtered by artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? songQuery = intentRequest.Intent.Slots?.TryGetValue("song", out var songSlot) == true ? songSlot.Value : null;
        string? musicianQuery = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlaySong: entered, locale={Locale}", locale);

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            return ResponseBuilder.Ask(ResponseStrings.Get("ElicitSongName", locale), new Reprompt(ResponseStrings.Get("ElicitSongName", locale)));
        }

        songQuery = StripSongCarrierPhrase(songQuery);

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            Logger.LogDebug("PlaySong: searching for artist filter='{Musician}'", musicianQuery);
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musicianQuery, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                cancellationToken).ConfigureAwait(false);

            Logger.LogDebug("PlaySong: artist search returned {Count} results for '{Musician}'", artists.Count, musicianQuery);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianQuery));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        // When the song query is a generic word like "musica"/"music" and we have
        // a valid artist, skip the song search and go straight to artist playback.
        // This avoids 1-4 wasted DB queries searching for a literal "music" song.
        if (GenericMusicWords.Contains(songQuery)
            && !string.IsNullOrWhiteSpace(musicianQuery) && artistsIds.Count > 0)
        {
            Logger.LogInformation(
                "PlaySong: song slot '{SongQuery}' is a generic music word, playing artist songs for '{Artist}'",
                songQuery, matchedArtistName);
            return await PlayArtistSongsFallback(
                artistsIds[0], matchedArtistName!, jellyfinUser!, user, session, context, locale, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<BaseItem> songs = await SearchWithAsrFallbackAsync(songQuery,
            searchTerm =>
            {
                var q = new InternalItemsQuery()
                {
                    User = jellyfinUser,
                    Recursive = true,
                    SearchTerm = searchTerm,
                    ArtistIds = artistsIds.ToArray(),
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(q, user, _libraryManager);
                return RetryAsync(() => _libraryManager.GetItemList(q), "GetSongs", cancellationToken);
            }).ConfigureAwait(false);
        Logger.LogDebug("PlaySong: Jellyfin returned {SongCount} songs for query='{SongQuery}'", songs.Count, songQuery);
        if (songs.Count == 0 && !string.IsNullOrWhiteSpace(musicianQuery) && artistsIds.Count > 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByNameAndArtist", locale, songQuery, matchedArtistName!));
        }
        else if (songs.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songQuery));
        }

        if (songs.Count > 1)
        {
            Logger.LogDebug("PlaySong: {Count} songs matched, running disambiguation", songs.Count);
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                songQuery,
                songs,
                s => s.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeSong,
                locale,
                best =>
                {
                    songs = new List<BaseItem> { best };
                    var qi = new List<QueueItem> { new() { Id = best.Id } };
                    session.NowPlayingQueue = qi;
                    session.FullNowPlayingItem = best;
                    string iid = best.Id.ToString();
                    int fuzzOffset = GetItemResumeOffset(best, jellyfinUser!, locale);
                    if (fuzzOffset > 0)
                    {
                        Logger.LogInformation(
                            "PlaySong fuzzy auto-play: resuming '{SongName}' from {OffsetMs}ms",
                            best.Name, fuzzOffset);
                    }

                    return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(iid, user), iid, best, user, context, fuzzOffset);
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                return missResponse!;
            }

            var matches = songs.Take(3).Select(s => (s.Id, s.Name, (string?)GetImageUrl(s.Id.ToString("N"), user))).ToList();
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        queueItems.Add(new QueueItem { Id = songs[0].Id });

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = songs[0];

        string item_id = songs[0].Id.ToString();

        int offsetMs = GetItemResumeOffset(songs[0], jellyfinUser!, locale);

        if (offsetMs > 0)
        {
            Logger.LogInformation(
                "PlaySong: resuming '{SongName}' from {OffsetMs}ms (saved position)",
                songs[0].Name, offsetMs);
        }

        Logger.LogDebug(
            "PlaySong: returning AudioPlayer, itemId={ItemId}, song='{SongName}', offsetMs={OffsetMs}",
            item_id, songs[0].Name, offsetMs);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, songs[0], user, context, offsetMs);
    }

    // Alexa's NLU can misalign slot boundaries, causing carrier phrases like
    // "la canzone" to bleed into the slot value. Strip them before searching.
    internal static string StripSongCarrierPhrase(string query)
    {
        string trimmed = query.TrimStart();
        foreach (string phrase in SongCarrierPhrases)
        {
            if (trimmed.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(phrase.Length).TrimStart();
            }
        }

        return query;
    }

    /// <summary>
    /// Check server-side playback progress for a single item and return resume offset in ms.
    /// Returns 0 if the item has no saved position or is marked as fully played.
    /// </summary>
    private int GetItemResumeOffset(BaseItem item, Jellyfin.Database.Implementations.Entities.User jellyfinUser, string locale)
    {
        UserItemData? data = _userDataManager.GetUserData(jellyfinUser, item);
        if (data == null)
        {
            Logger.LogDebug("PlaySong resume check: no UserItemData for '{SongName}'", item.Name);
            return 0;
        }

        Logger.LogDebug(
            "PlaySong resume check: '{SongName}' — PositionTicks={Ticks}, Played={Played}, IsFavorite={Fav}",
            item.Name, data.PlaybackPositionTicks, data.Played, data.IsFavorite);

        if (data.PlaybackPositionTicks > 0 && !data.Played)
        {
            int offset = (int)TimeSpan.FromTicks(data.PlaybackPositionTicks).TotalMilliseconds;
            Logger.LogInformation(
                "PlaySong resume check: '{SongName}' has saved position {Ticks} ticks ({OffsetMs}ms), will resume",
                item.Name, data.PlaybackPositionTicks, offset);
            return offset;
        }

        Logger.LogDebug(
            "PlaySong resume check: '{SongName}' — no resume needed (ticks={Ticks}, played={Played})",
            item.Name, data.PlaybackPositionTicks, data.Played);
        return 0;
    }

    /// <summary>
    /// Fallback when the song slot contains a generic music word (e.g. "musica"/"music")
    /// but the musician slot has a valid artist. Plays the artist's songs instead of
    /// returning "not found".
    /// </summary>
    private async Task<SkillResponse> PlayArtistSongsFallback(
        Guid artistId,
        string artistName,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        CancellationToken cancellationToken)
    {
        var artistSongsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artistId },
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        };
        ApplyLibraryFilter(artistSongsQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> artistItems = await RetryAsync(
            () => _libraryManager.GetItemList(artistSongsQuery),
            "GetArtistSongsFallback",
            cancellationToken).ConfigureAwait(false);

        Logger.LogDebug("PlaySong fallback: fetched {Count} songs for artist='{Artist}'", artistItems.Count, artistName);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, artistName));
        }

        var (sortedItems, startIndex, _) = SortAndFindResumeIndex(
            artistItems, jellyfinUser, _userDataManager, resumePosition: false);

        if (_config.ShuffleArtistSongs)
        {
            var shuffled = sortedItems.ToList();
            Shuffle(shuffled);
            sortedItems = shuffled;
            startIndex = 0;
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < sortedItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = sortedItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = sortedItems[startIndex];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            sortedItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        if (artistItems.Count >= ProgressiveQueueConstants.GetInitialFetchSize())
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artistId,
                    StartIndex = artistItems.Count,
                    TotalCount = int.MaxValue,
                    UserId = jellyfinUser.Id,
                    SortOrder = PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs
                });
        }

        string itemId = sortedItems[startIndex].Id.ToString();
        Logger.LogDebug(
            "PlaySong fallback: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}",
            itemId, startIndex, queueItems.Count);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, sortedItems[startIndex], user, context);
    }
}
