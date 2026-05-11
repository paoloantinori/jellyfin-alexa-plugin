using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for QueryArtistLibraryIntent requests.
/// Lets users ask about their library content by artist, e.g.
/// "Which tracks do we have by Artist X?" or "What albums of Artist X are available?".
/// </summary>
public class QueryArtistLibraryIntentHandler : BaseHandler
{
    private const int MaxListedItems = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public QueryArtistLibraryIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.QueryArtistLibrary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Query the library for content by a specific artist and return a spoken list.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? musician = null;
        string? queryType = null;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("musician", out Slot? musicianSlot))
            {
                musician = musicianSlot.Value;
            }

            if (intentRequest.Intent.Slots.TryGetValue("query_type", out Slot? queryTypeSlot))
            {
                queryType = queryTypeSlot.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchArtistName", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        IReadOnlyList<BaseItem> artists = await RetryAsync(
            () => _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                SearchTerm = musician,
                IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                Limit = 1,
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            }),
            "GetArtists",
            cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        Guid artistId = artists[0].Id;
        string artistName = artists[0].Name;

        if (IsAlbumQuery(queryType))
        {
            return await ListItemsByArtistAsync(
                artistId,
                artistName,
                jellyfinUser!,
                locale,
                new[] { BaseItemKind.MusicAlbum },
                null,
                "NoAlbumsByArtist",
                "AlbumsByArtistList",
                "AlbumsByArtistPartial",
                "GetArtistAlbums",
                context,
                user,
                cancellationToken).ConfigureAwait(false);
        }

        return await ListItemsByArtistAsync(
            artistId,
            artistName,
            jellyfinUser!,
            locale,
            null,
            new[] { MediaType.Audio },
            "NoSongsForArtist",
            "TracksByArtistList",
            "TracksByArtistPartial",
            "GetArtistTracks",
            context,
            user,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAlbumQuery(string? queryType)
    {
        if (string.IsNullOrWhiteSpace(queryType))
        {
            return false;
        }

        string normalized = queryType.ToLowerInvariant().Trim();
        return normalized.Contains("album", StringComparison.Ordinal)
            || normalized.Contains("records", StringComparison.Ordinal)
            || normalized.Contains("dischi", StringComparison.Ordinal)
            || normalized.Contains("disco", StringComparison.Ordinal);
    }

    private async Task<SkillResponse> ListItemsByArtistAsync(
        Guid artistId,
        string artistName,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        string locale,
        BaseItemKind[]? includeItemTypes,
        MediaType[]? mediaTypes,
        string emptyKey,
        string listKey,
        string partialKey,
        string operationName,
        Context? context,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ArtistIds = new[] { artistId },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true)
        };

        if (includeItemTypes != null)
        {
            query.IncludeItemTypes = includeItemTypes;
        }

        if (mediaTypes != null)
        {
            query.MediaTypes = mediaTypes;
        }

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), operationName, cancellationToken).ConfigureAwait(false);

        items = FavoritesFirst(items, jellyfinUser, _userDataManager);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get(emptyKey, locale, artistName));
        }

        int total = items.Count;
        SkillResponse response;

        if (total <= MaxListedItems)
        {
            string list = string.Join(", ", items.Select(i => i.Name));
            response = ResponseBuilder.Tell(ResponseStrings.Get(listKey, locale, artistName, total, list));
        }
        else
        {
            string partialList = string.Join(", ", items.Take(MaxListedItems).Select(i => i.Name));
            response = ResponseBuilder.Tell(ResponseStrings.Get(partialKey, locale, artistName, total, MaxListedItems, partialList));
        }

        var aplItems = items.Take(MaxListedItems).Select(i =>
            new Apl.ListDisplayItem(i.Name, i.Id.ToString("N"), artistName, GetImageUrl(i.Id.ToString("N"), user))).ToList();
        TryAttachListDirective(response, context, artistName, aplItems, "queryArtist");

        return response;
    }
}
