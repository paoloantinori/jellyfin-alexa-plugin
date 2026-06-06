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
    private const int VoicePageSize = 5;
    private static int MaxDisplayItems => Plugin.Instance?.Configuration?.MaxListDisplayItems ?? 15;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;

    public QueryArtistLibraryIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
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

        Logger.LogDebug("QueryArtistLibrary: entered, locale={Locale}, musician={Musician}, queryType={QueryType}", locale, musician, queryType);

        if (string.IsNullOrWhiteSpace(musician))
        {
            Logger.LogDebug("QueryArtistLibrary: missing musician slot, returning Tell");
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchArtistName", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
            musician, user, _libraryManager, _artistIndex, Logger,
            (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
            cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            Logger.LogDebug("QueryArtistLibrary: artist '{Musician}' not found", musician);
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        Guid artistId = artists[0].Id;
        string artistName = artists[0].Name;
        Logger.LogDebug("QueryArtistLibrary: matched artist '{ArtistName}' ({ArtistId}), isAlbumQuery={IsAlbum}", artistName, artistId, IsAlbumQuery(queryType));

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

        return SlotMappings.LibraryQueryTypeIsAlbum.TryGetValue(queryType.ToLowerInvariant().Trim(), out bool isAlbum) && isAlbum;
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
        ApplyLibraryFilter(query, user, _libraryManager);

        if (includeItemTypes != null)
        {
            query.IncludeItemTypes = includeItemTypes;
        }

        if (mediaTypes != null)
        {
            query.MediaTypes = mediaTypes;
        }

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), operationName, cancellationToken).ConfigureAwait(false);

        items = FavoritesAndRatingsFirst(items, jellyfinUser, _userDataManager);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get(emptyKey, locale, artistName));
        }

        int total = items.Count;
        int displayCount = Math.Min(total, MaxDisplayItems);
        bool isTruncated = total > VoicePageSize;
        SkillResponse response;

        if (total <= VoicePageSize)
        {
            string list = string.Join(", ", items.Select(i => i.Name));
            response = ResponseBuilder.Ask(
                ResponseStrings.Get(listKey, locale, artistName, total, list),
                new Reprompt(ResponseStrings.Get("CarouselReprompt", locale)));
        }
        else
        {
            string partialList = string.Join(", ", items.Take(VoicePageSize).Select(i => i.Name));
            string speech = ResponseStrings.Get(partialKey, locale, artistName, total, VoicePageSize, partialList);
            speech += " " + ResponseStrings.Get("ShowMorePrompt", locale);
            response = ResponseBuilder.Ask(speech, new Reprompt(ResponseStrings.Get("CarouselReprompt", locale)));

            // Store pagination state for ShowMoreIntent
            response.SessionAttributes = new Dictionary<string, object>();
            ListPaginationHelper.WriteState(
                response.SessionAttributes,
                ListPaginationHelper.ListType.ArtistLibrary,
                items.Take(displayCount).Select(i => i.Id.ToString()).ToArray(),
                VoicePageSize,
                VoicePageSize);
        }

        var aplItems = items.Take(displayCount).Select(i =>
            new Apl.ListDisplayItem(i.Name, i.Id.ToString("N"), artistName, GetImageUrl(i.Id.ToString("N"), user))).ToList();
        TryAttachCarouselDirective(response, context, artistName, aplItems, "queryArtist", locale: locale);

        return response;
    }
}
