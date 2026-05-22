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
using Jellyfin.Plugin.AlexaSkill.Alexa;
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
/// Handler for PlayPlaylist intents.
/// </summary>
public class PlayPlaylistIntentHandler : BaseHandler
{
    private ILibraryManager _libraryManager;
    private IUserManager _userManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayPlaylistIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayPlaylistIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayPlaylist, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a playlist by its name.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the playlist.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? playlistName = intentRequest.Intent.Slots?.TryGetValue("playlist", out var playlistSlot) == true ? playlistSlot.Value : null;

        if (string.IsNullOrWhiteSpace(playlistName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchPlaylistName", locale));
        }

        Logger.LogDebug("Play playlist: {0}", playlistName);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        InternalItemsQuery query = new InternalItemsQuery()
        {
            User = jellyfinUser,
            SearchTerm = playlistName,
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            DtoOptions = new DtoOptions(true),
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        QueryResult<BaseItem> playlists = await RetryAsync(() => SafeGetItemsResult(_libraryManager, query), "GetPlaylists", cancellationToken).ConfigureAwait(false);

        if (playlists.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
        }

        BaseItem? playlistMatch = null;
        if (playlists.TotalRecordCount > 1)
        {
            BaseItem? topMatch = FuzzyMatch(playlistName, playlists.Items, p => p.Name, user);
            if (topMatch != null)
            {
                playlistMatch = topMatch;
            }
            else
            {
                var (missOutcome, missResponse) = HandleFuzzyMiss(
                    playlistName,
                    playlists.Items,
                    p => p.Name,
                    best => new List<(Guid, string)> { (best.Id, best.Name) },
                    DisambiguationHelper.MediaTypePlaylist,
                    locale,
                    best =>
                    {
                        playlistMatch = best;
                        return null!;
                    },
                    user: user);

                if (missOutcome != FuzzyMissOutcome.NotFound)
                {
                    if (missResponse != null)
                    {
                        return missResponse;
                    }
                }
                else
                {
                    var matches = playlists.Items.Take(3).Select(p => (p.Id, p.Name, (string?)GetImageUrl(p.Id.ToString("N"), user))).ToList();
                    return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypePlaylist, locale, context);
                }
            }
        }
        else
        {
            playlistMatch = playlists.Items[0];
        }

        BaseItem playlist = playlistMatch!;

        // Get playlist items using the library manager for consistent pagination.
        // Fetch the first page for fast time-to-audio; rest is fetched on demand.
        QueryResult<BaseItem> playlistResult = SafeGetItemsResult(_libraryManager, new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = playlist.Id,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        });

        if (playlistResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistEmpty", locale));
        }

        IReadOnlyList<BaseItem> playlistItems = playlistResult.Items;

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < playlistItems.Count; i++)
        {
            BaseItem item = playlistItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
                PlaylistItemId = playlist.Id.ToString(),
            });
        }

        session.NowPlayingQueue = queueItems;

        BaseItem? firstItem = _libraryManager.GetItemById(queueItems[0].Id);

        // Persist queue to device storage for crash recovery
        if (firstItem != null)
        {
            _queueManager?.SetQueue(
                context.System.Device.DeviceID,
                playlistItems.Select(i => i.Id.ToString()).ToList(),
                0);
        }
        if (firstItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        session.FullNowPlayingItem = firstItem;

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        if (playlistResult.TotalRecordCount > playlistItems.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Playlist",
                    ParentId = playlist.Id,
                    PlaylistId = playlist.Id,
                    StartIndex = playlistItems.Count,
                    TotalCount = playlistResult.TotalRecordCount,
                    UserId = jellyfinUser!.Id
                });
        }

        string item_id = firstItem.Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, firstItem, user, context);
    }
}