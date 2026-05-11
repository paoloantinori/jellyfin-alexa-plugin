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
/// Handler for PlayArtistSongsIntent requests.
/// </summary>
public class PlayArtistSongsIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayArtistSongsIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayArtistSongsIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayArtistSongs, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific artist.
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
        string musician = intentRequest.Intent.Slots["musician"].Value;

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        IReadOnlyList<BaseItem> artists = await RetryAsync(
            () => _libraryManager.GetItemList(new InternalItemsQuery()
            {
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                DtoOptions = new DtoOptions(true)
            }),
            "GetArtists",
            cancellationToken).ConfigureAwait(false);
        if (artists.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        if (artists.Count > 1)
        {
            BaseItem? topMatch = FuzzyMatch(musician, artists, a => a.Name);
            if (topMatch == null)
            {
                var matches = artists.Take(3).Select(a => (a.Id, a.Name)).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeArtist, locale);
            }

            artists = new List<BaseItem> { topMatch };
        }

        string matchedArtistName = artists[0].Name;
        IReadOnlyList<BaseItem> artistsItems = await RetryAsync(
            () => _libraryManager.GetItemList(new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                MediaTypes = new[] { MediaType.Audio },
                OrderBy = PopularitySort,
                DtoOptions = new DtoOptions(true),
                ArtistIds = new[] { artists[0].Id }
            }),
            "GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        artistsItems = FavoritesFirst(artistsItems, jellyfinUser, _userDataManager);

        if (artistsItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, matchedArtistName));
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < artistsItems.Count; i++)
        {
            BaseItem item = artistsItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
            });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = artistsItems[0];

        string item_id = artistsItems[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, artistsItems[0], user, context);
    }
}