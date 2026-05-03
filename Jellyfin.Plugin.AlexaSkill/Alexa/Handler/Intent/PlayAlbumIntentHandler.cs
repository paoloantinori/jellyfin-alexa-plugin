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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayAlbumIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayAlbumIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayAlbum, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        if (intentRequest.DialogState != DialogStates.Completed)
        {
            return DelegateToDialog(intentRequest);
        }

        string album = intentRequest.Intent.Slots["album"].Value;
        string? musician = intentRequest.Intent.Slots["musician"].Value;

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        List<Guid> artistsIds = new List<Guid>();
        if (musician != null)
        {
            IReadOnlyList<BaseItem> artists = await RetryAsync(() => _libraryManager.GetItemList(new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            }), "GetArtists", cancellationToken).ConfigureAwait(false);
            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByArtist", locale, musician));
            }

            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        IReadOnlyList<BaseItem> albums = await RetryAsync(() => _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = album,
            ArtistIds = artistsIds.ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        }), "GetAlbums", cancellationToken).ConfigureAwait(false);
        if (albums.Count == 0 && musician != null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByNameAndArtist", locale, album, musician));
        }
        else if (albums.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByName", locale, album));
        }

        // If multiple albums found, ask for disambiguation
        if (albums.Count > 1)
        {
            var matches = albums.Take(3).Select(a => (a.Id, a.Name)).ToList();
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeAlbum, locale);
        }

        // Get all songs from the album
        IReadOnlyList<BaseItem> albumItems = await RetryAsync(() => _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            ParentId = albums[0].Id,
            MediaTypes = new[] { MediaType.Audio },
            DtoOptions = new DtoOptions(true),
        }), "GetAlbumTracks", cancellationToken).ConfigureAwait(false);
        if (albumItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album));
        }

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

        string item_id = albumItems[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, albumItems[0], user);
    }
}