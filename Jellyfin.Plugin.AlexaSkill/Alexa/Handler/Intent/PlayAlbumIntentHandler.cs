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
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayAlbumIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayAlbumIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
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

        if (string.IsNullOrWhiteSpace(album))
        {
            return ResponseBuilder.Ask(ResponseStrings.Get("ElicitAlbumName", locale), new Reprompt(ResponseStrings.Get("ElicitAlbumName", locale)));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;
        if (musician != null)
        {
            var artistSearchQuery = new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(artistSearchQuery, user, _libraryManager);

            IReadOnlyList<BaseItem> artists = await RetryAsync(
                () => _libraryManager.GetItemList(artistSearchQuery),
                "GetArtists",
                cancellationToken).ConfigureAwait(false);
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

        var albumSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = album,
            ArtistIds = artistsIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(albumSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> albums = await RetryAsync(
            () => _libraryManager.GetItemList(albumSearchQuery),
            "GetAlbums",
            cancellationToken).ConfigureAwait(false);
        if (albums.Count == 0 && musician != null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByNameAndArtist", locale, album, matchedArtistName!));
        }
        else if (albums.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByName", locale, album));
        }

        if (albums.Count > 1)
        {
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
                var matches = albums.Take(3).Select(a => (a.Id, a.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeAlbum, locale);
            }
        }

        // Get the first page of album tracks for fast time-to-audio.
        // Remaining tracks will be fetched on demand by PlaybackNearlyFinished.
        QueryResult<BaseItem> albumResult = await RetryAsync(
            () => _libraryManager.GetItemsResult(new InternalItemsQuery()
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
        if (albumResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album));
        }

        IReadOnlyList<BaseItem> albumItems = albumResult.Items;

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < albumItems.Count; i++)
        {
            BaseItem item = albumItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
            });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[0];

        // Persist queue to device storage for crash recovery
        _queueManager?.SetQueue(
            context.System.Device.DeviceID,
            albumItems.Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        if (albumResult.TotalRecordCount > albumItems.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Album",
                    ParentId = albums[0].Id,
                    StartIndex = albumItems.Count,
                    TotalCount = albumResult.TotalRecordCount,
                    UserId = jellyfinUser.Id
                });
        }

        string item_id = albumItems[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, albumItems[0], user, context);
    }
}