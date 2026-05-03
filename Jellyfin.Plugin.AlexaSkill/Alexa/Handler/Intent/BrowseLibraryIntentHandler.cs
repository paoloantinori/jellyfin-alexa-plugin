using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for BrowseLibraryIntent requests.
/// Browses the media library by category (artist/album/genre) and returns a spoken list of results.
/// </summary>
public class BrowseLibraryIntentHandler : BaseHandler
{
    private const int MaxResults = 5;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BrowseLibraryIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public BrowseLibraryIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.BrowseLibrary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Browse the media library by category and return a spoken list of results.
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

        string? browseCategory = null;
        string? filter = null;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("browse_category", out Slot? catSlot))
            {
                browseCategory = catSlot.Value;
            }

            if (intentRequest.Intent.Slots.TryGetValue("filter", out Slot? filterSlot))
            {
                filter = filterSlot.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(browseCategory))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchBrowseCategory", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        IReadOnlyList<BaseItem> items;

        switch (browseCategory.ToLowerInvariant())
        {
            case "artists":
                items = QueryItems(BaseItemKind.MusicArtist, filter, jellyfinUser);
                break;
            case "albums":
                items = QueryItems(BaseItemKind.MusicAlbum, filter, jellyfinUser);
                break;
            case "genres":
                return HandleGenresQuery(filter, locale, jellyfinUser);
            case "movies":
                items = QueryItems(BaseItemKind.Movie, filter, jellyfinUser);
                break;
            case "songs":
                items = QueryItems(BaseItemKind.Audio, filter, jellyfinUser);
                break;
            default:
                return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchBrowseCategory", locale));
        }

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoBrowseResults", locale, browseCategory));
        }

        return BuildListResponse(items, locale);
    }

    /// <summary>
    /// Queries items of a specific type with an optional search term filter.
    /// </summary>
    /// <param name="itemType">The type of items to query.</param>
    /// <param name="filter">Optional search term to filter results.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <returns>A list of matching items, limited to MaxResults.</returns>
    private IReadOnlyList<BaseItem> QueryItems(BaseItemKind itemType, string? filter, Jellyfin.Database.Implementations.Entities.User jellyfinUser)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { itemType },
            Limit = MaxResults,
            DtoOptions = new DtoOptions(true)
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query.SearchTerm = filter;
        }

        return _libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Handles the genres browse category. Genres require a filter; without one,
    /// prompts the user to specify a genre. With a filter, queries Audio items
    /// matching that genre and reports the count.
    /// </summary>
    /// <param name="filter">The genre filter string.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <returns>A skill response.</returns>
    private SkillResponse HandleGenresQuery(string? filter, string locale, Jellyfin.Database.Implementations.Entities.User jellyfinUser)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchBrowseCategory", locale));
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.Movie },
            Genres = new[] { filter },
            Limit = MaxResults,
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> items = _libraryManager.GetItemList(query);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoBrowseResults", locale, filter));
        }

        return BuildListResponse(items, locale);
    }

    /// <summary>
    /// Builds a spoken response listing up to MaxResults item names in a numbered list.
    /// </summary>
    /// <param name="items">The items to list.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <returns>A skill response with the numbered list.</returns>
    private static SkillResponse BuildListResponse(IReadOnlyList<BaseItem> items, string locale)
    {
        var itemEntries = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            itemEntries.Add(ResponseStrings.Get("BrowseItem", locale, (i + 1).ToString(CultureInfo.InvariantCulture), items[i].Name));
        }

        string listText = string.Join(". ", itemEntries);
        string speech = ResponseStrings.Get("BrowseResults", locale, items.Count.ToString(CultureInfo.InvariantCulture), listText);

        return ResponseBuilder.Tell(speech);
    }
}
