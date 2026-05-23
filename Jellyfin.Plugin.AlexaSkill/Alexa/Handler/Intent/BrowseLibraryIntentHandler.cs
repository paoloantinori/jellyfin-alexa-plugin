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
/// Handler for BrowseLibraryIntent requests.
/// Browses the media library by category (artist/album/genre) and returns a spoken list of results.
/// </summary>
public class BrowseLibraryIntentHandler : BaseHandler
{
    private const int VoicePageSize = 5;

    private static int MaxDisplayItems => Plugin.Instance?.Configuration?.MaxListDisplayItems ?? 15;

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
        if (IfFeatureDisabled(c => c.BrowseLibraryEnabled, request) is { } disabled)
        {
            return disabled;
        }

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

        Logger.LogDebug("BrowseLibrary: entered, locale={Locale}, browseCategory={Category}, filter={Filter}", locale, browseCategory, filter);

        if (string.IsNullOrWhiteSpace(browseCategory))
        {
            Logger.LogDebug("BrowseLibrary: returning Ask (missing category)");
            string prompt = ResponseStrings.Get("DidNotCatchBrowseCategory", locale);
            return ResponseBuilder.Ask(prompt, new Reprompt(prompt));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        Jellyfin.Database.Implementations.Entities.User resolvedUser = jellyfinUser!;

        string normalized = browseCategory.ToLowerInvariant();

        if (SlotMappings.IsGenreCategory(normalized))
        {
            Logger.LogDebug("BrowseLibrary: resolved as genre query, filter={Filter}", filter);
            return await HandleGenresQuery(filter, locale, resolvedUser, context, user, cancellationToken).ConfigureAwait(false);
        }

        if (!SlotMappings.BrowseCategoryToItemKind.TryGetValue(normalized, out BaseItemKind? itemKind) || !itemKind.HasValue)
        {
            Logger.LogDebug("BrowseLibrary: unrecognized category '{Category}', returning Ask", normalized);
            string prompt = ResponseStrings.Get("DidNotCatchBrowseCategory", locale);
            return ResponseBuilder.Ask(prompt, new Reprompt(prompt));
        }

        Logger.LogDebug("BrowseLibrary: resolved category to {ItemKind}", itemKind.Value);
        IReadOnlyList<BaseItem> items = await QueryItems(itemKind.Value, filter, resolvedUser, user, cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            Logger.LogDebug("BrowseLibrary: no results for {Category}, returning Tell", browseCategory);
            return ResponseBuilder.Tell(ResponseStrings.Get("NoBrowseResults", locale, browseCategory));
        }

        Logger.LogDebug("BrowseLibrary: found {ItemCount} items, building list response", items.Count);
        return BuildListResponse(items, locale, browseCategory, context, user);
    }

    /// <summary>
    /// Queries items of a specific type with an optional search term filter.
    /// </summary>
    /// <param name="itemType">The type of items to query.</param>
    /// <param name="filter">Optional search term to filter results.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <returns>A list of matching items, limited to MaxDisplayItems.</returns>
    private async Task<IReadOnlyList<BaseItem>> QueryItems(BaseItemKind itemType, string? filter, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Entities.User user, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = FilterByContentAccess(new[] { itemType }),
            Limit = MaxDisplayItems,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            query.SearchTerm = filter;
        }

        return await RetryAsync(() => _libraryManager.GetItemList(query), $"Get{itemType}Items", cancellationToken).ConfigureAwait(false);
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
    private async Task<SkillResponse> HandleGenresQuery(string? filter, string locale, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Context context, Entities.User user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchBrowseCategory", locale));
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.Movie }),
            Genres = new[] { filter },
            Limit = MaxDisplayItems,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetGenreItems", cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoBrowseResults", locale, filter));
        }

        return BuildListResponse(items, locale, filter, context, user);
    }

    private SkillResponse BuildListResponse(IReadOnlyList<BaseItem> items, string locale, string browseCategory, Context? context, Entities.User user)
    {
        int total = items.Count;
        int voiceCount = Math.Min(total, VoicePageSize);
        int displayCount = Math.Min(total, MaxDisplayItems);
        bool isTruncated = total > voiceCount;

        Logger.LogDebug("BrowseLibrary: BuildListResponse total={Total}, voiceCount={VoiceCount}, isTruncated={IsTruncated}", total, voiceCount, isTruncated);

        var voiceEntries = new List<string>();
        for (int i = 0; i < voiceCount; i++)
        {
            voiceEntries.Add(ResponseStrings.Get("BrowseItem", locale, (i + 1).ToString(CultureInfo.InvariantCulture), items[i].Name));
        }

        string voiceListText = string.Join(". ", voiceEntries);

        string speech;
        if (isTruncated)
        {
            speech = ResponseStrings.Get("BrowseResultsPartial", locale, total.ToString(CultureInfo.InvariantCulture), browseCategory, voiceCount.ToString(CultureInfo.InvariantCulture), voiceListText);
            speech += " " + ResponseStrings.Get("ShowMorePrompt", locale);
        }
        else
        {
            speech = ResponseStrings.Get("BrowseResults", locale, total.ToString(CultureInfo.InvariantCulture), voiceListText);
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
                ListPaginationHelper.ListType.BrowseLibrary,
                items.Take(displayCount).Select(i => i.Id.ToString()).ToArray(),
                voiceCount,
                VoicePageSize);
        }

        string title = char.ToUpper(browseCategory[0], CultureInfo.InvariantCulture) + browseCategory[1..];
        var aplItems = items.Take(displayCount).Select(i =>
            new Apl.ListDisplayItem(i.Name, i.Id.ToString("N"), GetArtistSubtitle(i), GetImageUrl(i.Id.ToString("N"), user))).ToList();
        TryAttachCarouselDirective(response, context, title, aplItems, locale: locale);

        return response;
    }
}
