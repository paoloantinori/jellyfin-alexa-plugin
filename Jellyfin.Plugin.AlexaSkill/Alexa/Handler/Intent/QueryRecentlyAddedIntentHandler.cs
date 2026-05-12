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
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for QueryRecentlyAddedIntent. Reads back recently added items
/// without auto-playing them, letting the user pick one by number.
/// </summary>
public class QueryRecentlyAddedIntentHandler : BaseHandler
{
    private const int MaxResults = 10;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryRecentlyAddedIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public QueryRecentlyAddedIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.QueryRecentlyAdded, StringComparison.Ordinal);
    }

    /// <summary>
    /// Query recently added items and read them back as a numbered list.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response listing the recently added items.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        Jellyfin.Database.Implementations.Entities.User resolvedUser = jellyfinUser!;

        var query = new InternalItemsQuery
        {
            User = resolvedUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicAlbum },
            Limit = MaxResults,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };

        IReadOnlyList<BaseItem> recentItems = await RetryAsync(() => _libraryManager.GetItemList(query), "GetRecentlyAddedItems", cancellationToken).ConfigureAwait(false);

        if (recentItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("QueryRecentlyAddedEmpty", locale));
        }

        return BuildListResponse(recentItems, locale, context, user);
    }

    private SkillResponse BuildListResponse(IReadOnlyList<BaseItem> items, string locale, Context? context, Entities.User user)
    {
        var itemEntries = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            string? artist = GetArtistSubtitle(item);
            string itemName = EscapeXml(item.Name ?? string.Empty);

            string entry;
            if (!string.IsNullOrEmpty(artist))
            {
                entry = ResponseStrings.Get("QueryRecentlyAddedItem", locale, (i + 1).ToString(CultureInfo.InvariantCulture), itemName, artist);
            }
            else
            {
                entry = ResponseStrings.Get("QueryRecentlyAddedItemNoArtist", locale, (i + 1).ToString(CultureInfo.InvariantCulture), itemName);
            }

            itemEntries.Add(entry);
        }

        string listText = string.Join(". ", itemEntries);
        string intro = ResponseStrings.Get("QueryRecentlyAddedIntro", locale, items.Count.ToString(CultureInfo.InvariantCulture));
        string prompt = ResponseStrings.Get("QueryRecentlyAddedPrompt", locale);

        string speech = $"{intro} {listText}. {prompt}";

        SkillResponse response = ResponseBuilder.Tell(speech);

        var aplItems = items.Select(i =>
            new ListDisplayItem(i.Name ?? string.Empty, i.Id.ToString("N"), GetArtistSubtitle(i), GetImageUrl(i.Id.ToString("N"), user))).ToList();
        TryAttachListDirective(response, context, "Recently Added", aplItems, "recentlyAdded");

        return response;
    }
}
