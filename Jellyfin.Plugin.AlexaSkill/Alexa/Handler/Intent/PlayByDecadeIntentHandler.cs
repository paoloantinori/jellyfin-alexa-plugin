using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayByDecadeIntent requests. Plays media from a specific decade/era.
/// </summary>
public class PlayByDecadeIntentHandler : BaseHandler
{
    private const int MaxQueryResults = 500;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    private static readonly Dictionary<string, int> WordDecadeMap = new()
    {
        { "fifties", 1950 },
        { "sixties", 1960 },
        { "seventies", 1970 },
        { "eighties", 1980 },
        { "nineties", 1990 },
        { "noughties", 2000 },
        { "two thousands", 2000 },
        { "twenty tens", 2010 },
        { "twenty twenties", 2020 },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayByDecadeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayByDecadeIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayByDecade, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific decade.
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

        string? decadeSlot = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("decade", out Slot? decadeSlotObj))
        {
            decadeSlot = decadeSlotObj.Value;
        }

        if (string.IsNullOrEmpty(decadeSlot))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchDecade", locale));
        }

        int[]? years = ParseDecadeYears(decadeSlot);
        if (years == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchDecade", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            Limit = MaxQueryResults,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            Years = years,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            DtoOptions = new DtoOptions(true)
        };

        // Optional genre filter if provided
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("genre", out Slot? genreSlotObj) && !string.IsNullOrEmpty(genreSlotObj.Value))
        {
            query.Genres = new[] { genreSlotObj.Value };
        }

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetDecadeItems", cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundDecade", locale, decadeSlot));
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < items.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = items[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = items[0];

        string itemId = items[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, items[0], user);
    }

    /// <summary>
    /// Parse a decade string into an array of years for InternalItemsQuery.Years.
    /// Handles formats like: "80s", "eighties", "1980s", "1980", "the 80s".
    /// </summary>
    /// <param name="decade">The decade string to parse.</param>
    /// <returns>An array of years in the decade, or null if parsing fails.</returns>
    internal static int[]? ParseDecadeYears(string decade)
    {
        if (string.IsNullOrWhiteSpace(decade))
        {
            return null;
        }

        decade = decade.Trim().ToLowerInvariant();

        // Remove common prefix
        if (decade.StartsWith("the ", StringComparison.Ordinal))
        {
            decade = decade[4..];
        }

        // Check word-form decades first (e.g. "eighties", "the two thousands")
        if (WordDecadeMap.TryGetValue(decade, out int wordDecadeStart))
        {
            return BuildDecadeYears(wordDecadeStart);
        }

        // Remove common suffixes for numeric forms
        decade = decade.Replace("'s", string.Empty, StringComparison.Ordinal)
                       .TrimEnd('s');

        // Try direct numeric parse (handles "80", "1980", etc.)
        if (int.TryParse(decade, NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            int decadeStart;

            if (year >= 1000)
            {
                decadeStart = (year / 10) * 10;
            }
            else if (year <= 99)
            {
                // Short form like "80" -> derive century
                int century = year <= 29 ? 2000 : 1900;
                decadeStart = ((century + year) / 10) * 10;
            }
            else
            {
                return null;
            }

            return BuildDecadeYears(decadeStart);
        }

        return null;
    }

    private static int[] BuildDecadeYears(int decadeStart)
    {
        var years = new int[10];
        for (int i = 0; i < 10; i++)
        {
            years[i] = decadeStart + i;
        }

        return years;
    }
}
