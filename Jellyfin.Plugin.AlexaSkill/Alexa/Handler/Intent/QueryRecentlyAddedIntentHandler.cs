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
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
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
    private const int VoicePageSize = 5;

    private static int MaxResults => Plugin.Instance?.Configuration?.MaxRecentlyAddedResults ?? 10;
    private static int MaxDisplayItems => Plugin.Instance?.Configuration?.MaxListDisplayItems ?? 15;

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
        ApplyLibraryFilter(query, user, _libraryManager);

        SearchResultCache cache = Plugin.Instance?.SearchCache ?? SearchResultCache.Noop;
        IReadOnlyList<BaseItem> recentItems = await cache.GetRecentlyAddedCachedAsync(
            resolvedUser.Id,
            () => RetryAsync(() => _libraryManager.GetItemList(query), "GetRecentlyAddedItems", cancellationToken)).ConfigureAwait(false);

        if (recentItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("QueryRecentlyAddedEmpty", locale));
        }

        return BuildListResponse(recentItems, locale, context, user);
    }

    private SkillResponse BuildListResponse(IReadOnlyList<BaseItem> items, string locale, Context? context, Entities.User user)
    {
        int total = items.Count;
        int voiceCount = Math.Min(total, VoicePageSize);
        int displayCount = Math.Min(total, MaxDisplayItems);
        bool isTruncated = total > voiceCount;

        var voiceEntries = new List<string>();
        for (int i = 0; i < voiceCount; i++)
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

            voiceEntries.Add(entry);
        }

        string voiceListText = string.Join(". ", voiceEntries);
        string intro = ResponseStrings.Get("QueryRecentlyAddedIntro", locale, total.ToString(CultureInfo.InvariantCulture));
        string prompt = ResponseStrings.Get("QueryRecentlyAddedPrompt", locale);

        string speech;
        if (isTruncated)
        {
            speech = ResponseStrings.Get("QueryRecentlyAddedPartial", locale, intro, voiceCount.ToString(CultureInfo.InvariantCulture), voiceListText) + " " + ResponseStrings.Get("ShowMorePrompt", locale) + " " + prompt;
        }
        else
        {
            speech = $"{intro} {voiceListText}. {prompt}";
        }

        SkillResponse response = isTruncated
            ? ResponseBuilder.Ask(speech, new Reprompt(ResponseStrings.Get("ShowMorePrompt", locale)))
            : ResponseBuilder.Tell(speech);

        // Store pagination state for ShowMoreIntent when truncated
        if (isTruncated)
        {
            response.SessionAttributes = new Dictionary<string, object>();
            ListPaginationHelper.WriteState(
                response.SessionAttributes,
                ListPaginationHelper.ListType.RecentlyAdded,
                items.Take(displayCount).Select(i => i.Id.ToString()).ToArray(),
                voiceCount,
                VoicePageSize);
        }

        var aplItems = items.Take(displayCount).Select(i =>
            new ListDisplayItem(i.Name ?? string.Empty, i.Id.ToString("N"), GetArtistSubtitle(i), GetImageUrl(i.Id.ToString("N"), user))).ToList();
        TryAttachListDirective(response, context, "Recently Added", aplItems, "recentlyAdded", hasMore: isTruncated);

        return response;
    }
}
