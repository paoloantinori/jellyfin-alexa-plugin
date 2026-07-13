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
/// Handler for PlayAlbumIntent requests.
/// </summary>
public class PlayAlbumIntentHandler : BaseHandler
{
    private ILibraryManager _libraryManager;
    private IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Minimum album-query length to attempt the full-catalog fuzzy fallback. Shorter
    /// queries (e.g. "red", "aria") produce too many substring false positives.
    /// </summary>
    private const int MinFuzzyAlbumQueryLength = 4;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayAlbumIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    public PlayAlbumIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayAlbum, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a specific album by name, optionally filtered by artist.
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

        string? album = intentRequest.Intent.Slots?.TryGetValue("album", out var albumSlot) == true ? albumSlot.Value : null;
        string? musician = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlayAlbum: entered, locale={Locale}", locale);

        if (string.IsNullOrWhiteSpace(album))
        {
            return ResponseBuilder.Ask(ResponseStrings.Get("ElicitAlbumName", locale), new Reprompt(ResponseStrings.Get("ElicitAlbumName", locale)));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musician))
        {
            Logger.LogDebug("PlayAlbum: searching for artist filter='{Musician}'", musician);
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musician, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                cancellationToken).ConfigureAwait(false);

            Logger.LogDebug("PlayAlbum: artist search returned {Count} results for '{Musician}'", artists.Count, musician);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByArtist", locale, musician));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        var albumSearchQuery = BuildAlbumQuery(jellyfinUser, user, album, artistsIds.ToArray());

        Logger.LogDebug("PlayAlbum: querying Jellyfin with searchTerm='{Album}', artistIds={ArtistIdsCount}, types=MusicAlbum", album, artistsIds.Count);
        IReadOnlyList<BaseItem> albums = await RetryAsync(
            () => _libraryManager.GetItemList(albumSearchQuery),
            "GetAlbums",
            cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("PlayAlbum: Jellyfin returned {ResultCount} albums", albums.Count);
        if (albums.Count == 0 && !string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByNameAndArtist", locale, album, matchedArtistName!));
        }

        if (albums.Count == 0 && album.Length >= MinFuzzyAlbumQueryLength)
        {
            // Fuzzy fallback: ASR may transcribe the album name with accents or
            // Italian-vs-English spelling that Jellyfin's search index doesn't normalize
            // (e.g. "caffè" vs "Cafe"). Match against the user's albums via FuzzyMatcher
            // partial-ratio (Levenshtein, NOT Double Metaphone — true phonetic matching
            // would need a precomputed album index, cf. ArtistIndexService). JF-336.
            // Min-length guard: very short queries produce too many substring false
            // positives across a full-catalog scan (e.g. "red", "aria") — skip them.
            Logger.LogDebug("PlayAlbum: exact search miss, trying fuzzy fallback for '{Query}'", album);
            var phoneticAlbumQuery = BuildAlbumQuery(jellyfinUser, user, searchTerm: null, artistIds: null);
            IReadOnlyList<BaseItem> allAlbums = await RetryAsync(
                () => _libraryManager.GetItemList(phoneticAlbumQuery),
                "GetAlbumsPhonetic",
                cancellationToken).ConfigureAwait(false);

            // FindBestMatchWithScore (single best). NOTE: a multi-match here currently
            // auto-plays the best via HandleFuzzyMiss (which re-scores + auto-accepts at
            // >= GetDefaultThreshold); real disambiguation for different-name collisions
            // (e.g. several "Greatest Hits" by different artists) needs a HandleFuzzyMiss
            // bypass — tracked in JF-341. RankMatches was tried (b12cf5c) but is inert
            // here for that reason.
            var fuzzyMatch = FuzzyMatcher.FindBestMatchWithScore(album, allAlbums, a => a.Name);
            if (fuzzyMatch.HasValue && fuzzyMatch.Value.Score >= FuzzyMatcher.GetDefaultThreshold(user))
            {
                Logger.LogInformation(
                    "PlayAlbum: fuzzy fallback matched album '{Name}' score={Score} for query='{Query}'",
                    fuzzyMatch.Value.Item.Name, fuzzyMatch.Value.Score, album);
                albums = new List<BaseItem> { fuzzyMatch.Value.Item };
            }
        }

        if (albums.Count == 0)
        {
            // Cross-media artist fallback: the NLU may have routed an artist name to
            // PlayAlbumIntent. Only fire on a HIGH-confidence artist match — a weak match
            // (e.g. "jazz caffè"→"Uazz" @75) must NOT play the wrong artist; report the
            // album as not found instead. JF-336 (was GetDefaultThreshold=60).
            Logger.LogDebug("PlayAlbum: no albums found, trying artist fallback with query='{Query}'", album);
            IReadOnlyList<BaseItem> fallbackArtists = await Util.ArtistSearch.SearchAsync(
                album, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtistsFallback", ct),
                cancellationToken).ConfigureAwait(false);

            if (fallbackArtists.Count > 0)
            {
                // Single best match (FindBestMatchWithScore), NOT RankMatches: this is a
                // cross-media guess (album not found → play an artist instead), so only one
                // strong match should fire — disambiguating among artist guesses is wrong UX.
                var bestMatch = FuzzyMatcher.FindBestMatchWithScore(album, fallbackArtists, a => a.Name);
                if (bestMatch.HasValue && bestMatch.Value.Score >= FuzzyMatcher.ContainmentScore)
                {
                    BaseItem artist = bestMatch.Value.Item;
                    Logger.LogInformation(
                        "PlayAlbum: artist fallback found '{ArtistName}' with score={Score} for query='{Query}'",
                        artist.Name, bestMatch.Value.Score, album);

                    return await PlayArtistSongsFromAlbumFallback(
                        artist.Id, artist.Name, jellyfinUser!, user, session, context, locale, cancellationToken,
                        announcement: ResponseStrings.Get("FoundArtistInstead", locale, artist.Name)).ConfigureAwait(false);
                }
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByName", locale, album));
        }

        if (albums.Count > 1)
        {
            Logger.LogDebug("PlayAlbum: {Count} albums matched, running disambiguation", albums.Count);
            BaseItem? albumMatch = null;
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                album,
                albums,
                a => a.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeAlbum,
                locale,
                best =>
                {
                    albumMatch = best;
                    return null!;
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                albums = new List<BaseItem> { albumMatch! };
            }
            else
            {
                var matches = albums.Take(3).Select(a => (a.Id, a.Name, (string?)GetImageUrl(a.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeAlbum, locale, context);
            }
        }

        // Get the first page of album tracks for fast time-to-audio.
        // Remaining tracks will be fetched on demand by PlaybackNearlyFinished.
        Logger.LogDebug("PlayAlbum: querying tracks for album='{AlbumName}' (id={AlbumId})", albums[0].Name, albums[0].Id);
        QueryResult<BaseItem> albumResult = await RetryAsync(
            () => SafeGetItemsResult(_libraryManager, new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                ParentId = albums[0].Id,
                MediaTypes = new[] { MediaType.Audio },
                DtoOptions = new DtoOptions(true),
                Limit = ProgressiveQueueConstants.GetInitialFetchSize()
            }),
            "GetAlbumTracks",
            cancellationToken).ConfigureAwait(false);
        Logger.LogDebug("PlayAlbum: Jellyfin returned {TrackCount} tracks (total={TotalCount})", albumResult.Items.Count, albumResult.TotalRecordCount);
        if (albumResult.TotalRecordCount == 0)
        {
            // Tolerant fallback: for split / multi-disc / malformed-folder albums, the
            // folder-based ParentId query can return 0 even when the tracks exist (the
            // track's Album metadata still links them). Query by album membership, which
            // ignores folder structure. Verified on the malformed "Jazz Cafe" album:
            // ParentId+Recursive returns 0, AlbumIds returns all tracks. JF-338.
            Logger.LogDebug("PlayAlbum: folder-based track query returned 0, retrying by AlbumIds for '{Name}'", albums[0].Name);
            albumResult = await RetryAsync(
                () => SafeGetItemsResult(_libraryManager, new InternalItemsQuery()
                {
                    User = jellyfinUser,
                    Recursive = true,
                    AlbumIds = new[] { albums[0].Id },
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true),
                    Limit = ProgressiveQueueConstants.GetInitialFetchSize()
                }),
                "GetAlbumTracksByAlbumIds",
                cancellationToken).ConfigureAwait(false);
            Logger.LogDebug("PlayAlbum: AlbumIds fallback returned {TrackCount} tracks (total={TotalCount})", albumResult.Items.Count, albumResult.TotalRecordCount);
        }

        if (albumResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album));
        }

        IReadOnlyList<BaseItem> albumItems = albumResult.Items;

        // Check for existing queue position from server-side progress
        (int startIndex, _) = FindResumeTrackIndex(
            albumItems, jellyfinUser!, _userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            Logger.LogInformation(
                "PlayAlbum: resuming queue from track {Index} ({Name})",
                startIndex, albumItems[startIndex].Name);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < albumItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = albumItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[startIndex];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            albumItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest.
        // StartIndex uses the original page size because the database offset is
        // independent of the resume slice.
        if (albumResult.TotalRecordCount > albumResult.Items.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Album",
                    ParentId = albums[0].Id,
                    StartIndex = albumResult.Items.Count,
                    TotalCount = albumResult.TotalRecordCount,
                    UserId = jellyfinUser!.Id
                });
        }

        string item_id = albumItems[startIndex].Id.ToString();

        Logger.LogDebug(
            "PlayAlbum: returning AudioPlayer, itemId={ItemId}, album='{AlbumName}', startIndex={StartIndex}, queueSize={QueueSize}",
            item_id, albums[0].Name, startIndex, queueItems.Count);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, albumItems[startIndex], user, context);
    }

    /// <summary>
    /// Builds a MusicAlbum query scoped to the user's libraries (with library filtering).
    /// Pass a search term for the exact lookup, or null for the broad fuzzy-fallback scan.
    /// </summary>
    private InternalItemsQuery BuildAlbumQuery(Jellyfin.Database.Implementations.Entities.User? jellyfinUser, Jellyfin.Plugin.AlexaSkill.Entities.User user, string? searchTerm, Guid[]? artistIds)
    {
        var q = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            q.SearchTerm = searchTerm;
        }

        if (artistIds is { Length: > 0 })
        {
            q.ArtistIds = artistIds;
        }

        ApplyLibraryFilter(q, user, _libraryManager);
        return q;
    }

    /// <summary>
    /// Cross-media-type fallback: when no album is found, plays the matched artist's songs.
    /// Delegates to <see cref="BaseHandler.BuildArtistSongsResponseAsync"/>.
    /// </summary>
    private Task<SkillResponse> PlayArtistSongsFromAlbumFallback(
        Guid artistId,
        string artistName,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        CancellationToken cancellationToken,
        string? announcement = null)
    {
        return BuildArtistSongsResponseAsync(
            artistId, artistName, jellyfinUser, user, session, context, locale,
            _libraryManager, _userDataManager, _queueManager,
            "PlayAlbum fallback",
            announcement, cancellationToken);
    }
}