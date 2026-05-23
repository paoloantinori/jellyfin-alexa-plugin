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
/// Handler for InProgressMediaListIntent. Lists the user's in-progress media items (read-only informational, no playback).
/// </summary>
public class InProgressMediaListIntentHandler : BaseHandler
{
    private const int MaxCandidates = 50;
    private const int VoicePageSize = 5;

    private static int MaxDisplayItems => Plugin.Instance?.Configuration?.MaxInProgressDisplayItems ?? 10;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="InProgressMediaListIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public InProgressMediaListIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.InProgressMediaList, StringComparison.Ordinal);
    }

    /// <summary>
    /// List the user's in-progress media items.
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
            IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode },
            IsPlayed = false,
            MinDateLastSavedForUser = DateTime.UtcNow.AddDays(-30),
            Limit = MaxCandidates,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        IReadOnlyList<BaseItem> recentItems = await RetryAsync(() => _libraryManager.GetItemList(query), "GetRecentItems", cancellationToken).ConfigureAwait(false);

        var inProgressItems = new List<(BaseItem Item, string Position)>();

        foreach (BaseItem item in recentItems)
        {
            UserItemData? userData = _userDataManager.GetUserData(resolvedUser, item);
            if (userData == null || userData.PlaybackPositionTicks <= 0)
            {
                continue;
            }

            inProgressItems.Add((item, FormatPosition(userData.PlaybackPositionTicks)));

            if (inProgressItems.Count >= MaxDisplayItems)
            {
                break;
            }
        }

        if (inProgressItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoInProgressMedia", locale));
        }

        int total = inProgressItems.Count;
        int voiceCount = Math.Min(total, VoicePageSize);
        bool isTruncated = total > voiceCount;

        var voiceDescriptions = new List<string>();
        for (int i = 0; i < voiceCount; i++)
        {
            voiceDescriptions.Add(ResponseStrings.Get("InProgressItemWithPosition", locale, inProgressItems[i].Item.Name, inProgressItems[i].Position));
        }

        string voiceListText = string.Join(". ", voiceDescriptions);

        string speech;
        if (isTruncated)
        {
            speech = ResponseStrings.Get("InProgressListPartial", locale, total, voiceCount, voiceListText);
            speech += " " + ResponseStrings.Get("ShowMorePrompt", locale);
        }
        else
        {
            speech = ResponseStrings.Get("InProgressList", locale, total, voiceListText);
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
                ListPaginationHelper.ListType.InProgress,
                inProgressItems.Select(i => i.Item.Id.ToString()).ToArray(),
                voiceCount,
                VoicePageSize);
        }

        var aplItems = inProgressItems.Select(i =>
            new Apl.ListDisplayItem(i.Item.Name, i.Item.Id.ToString("N"), i.Position, GetImageUrl(i.Item.Id.ToString("N"), user))).ToList();
        TryAttachListDirective(response, context, "In Progress", aplItems, "inProgress", hasMore: isTruncated);

        return response;
    }
}
