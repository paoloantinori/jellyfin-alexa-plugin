using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayLastAdded intents. Supports optional time context
/// to filter recently added items by period (today, this week, this month).
/// </summary>
public class PlayLastAddedIntentHandler : BaseHandler
{
    private ILibraryManager _libraryManager;
    private IUserManager _userManager;

    private static readonly Dictionary<string, (int Days, string LocaleKey)> TimePeriodMap = new()
    {
        { "today", (1, "TimePeriodToday") },
        { "this_week", (7, "TimePeriodThisWeek") },
        { "this_month", (30, "TimePeriodThisMonth") }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayLastAddedIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayLastAddedIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayLastAdded, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play recently added items, optionally filtered by a time period slot.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the last added items.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        int lookbackDays = 3;
        string? timeLabel = null;

        if (intentRequest.Intent.Slots != null
            && intentRequest.Intent.Slots.TryGetValue("time_period", out Slot? timeSlot)
            && !string.IsNullOrEmpty(timeSlot.Value))
        {
            string periodId = timeSlot.Value.ToLowerInvariant();
            if (TimePeriodMap.TryGetValue(periodId, out var period))
            {
                lookbackDays = period.Days;
                timeLabel = ResponseStrings.Get(period.LocaleKey, locale);
            }
        }

        string searchingText = timeLabel != null
            ? ResponseStrings.Get("SearchingMediaTime", locale, timeLabel)
            : ResponseStrings.Get("SearchingMedia", locale);
        await SendProgressiveResponse(context, request, searchingText).ConfigureAwait(false);

        InternalItemsQuery query = new InternalItemsQuery()
        {
            User = _userManager.GetUserById(session.UserId),
            MinDateLastSavedForUser = DateTime.UtcNow.Date.AddDays(-lookbackDays),
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true)
        };

        IReadOnlyList<BaseItem> latestItems = await RetryAsync(() => _libraryManager.GetItemList(query), "GetLatestItems", cancellationToken).ConfigureAwait(false);

        if (latestItems.Count == 0)
        {
            if (timeLabel != null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NoNewlyAddedItemsTime", locale, timeLabel));
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NoNewlyAddedItems", locale));
        }

        List<QueueItem> queueItems = new List<QueueItem>();

        for (int i = 0; i < latestItems.Count; i++)
        {
            BaseItem item = latestItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
                PlaylistItemId = null,
            });
        }

        session.NowPlayingQueue = queueItems;

        BaseItem? prevItem = _libraryManager.GetItemById(latestItems[0].Id);
        if (prevItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        session.FullNowPlayingItem = prevItem;

        string item_id = prevItem.Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, prevItem, user);
    }
}
