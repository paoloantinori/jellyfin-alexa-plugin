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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayFavorites intents. Supports playing the authenticated user's
/// favorites or another user's favorites by name (e.g. "Play Paolo's favourites").
/// </summary>
public class PlayFavoritesIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayFavoritesIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayFavoritesIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayFavorites, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play favorite items for the authenticated user or another user specified by name.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Play directive of the favorite items.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        // Check if a username slot was provided
        string? usernameSlot = intentRequest.Intent.Slots?.TryGetValue("username", out var slot) == true ? slot.Value : null;

        JellyfinUser? jellyfinUser;

        if (!string.IsNullOrWhiteSpace(usernameSlot))
        {
            // Resolve the target user by fuzzy matching against all Jellyfin users
            jellyfinUser = ResolveUserByName(usernameSlot);
            if (jellyfinUser == null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("UserByNameNotFound", locale, usernameSlot));
            }

            Logger.LogInformation("Playing favorites for user {Username} (requested by {RequestedBy})", jellyfinUser.Username, usernameSlot);
        }
        else
        {
            // Default: use the authenticated user
            jellyfinUser = _userManager.GetUserById(session.UserId);
            if (jellyfinUser == null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("UserNotFound", locale));
            }
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        InternalItemsQuery query = new InternalItemsQuery()
        {
            User = jellyfinUser,
            IsFavorite = true,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true)
        };
        ApplyLibraryFilter(query, user);

        IReadOnlyList<BaseItem> favoriteItems = await RetryAsync(() => _libraryManager.GetItemList(query), "GetFavoriteItems", cancellationToken).ConfigureAwait(false);

        if (favoriteItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoFavoriteItems", locale));
        }

        List<QueueItem> queueItems = new List<QueueItem>();

        for (int i = 0; i < favoriteItems.Count; i++)
        {
            BaseItem item = favoriteItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
                PlaylistItemId = null,
            });
        }

        session.NowPlayingQueue = queueItems;

        BaseItem? firstItem = _libraryManager.GetItemById(queueItems[0].Id);
        if (firstItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        session.FullNowPlayingItem = firstItem;

        string item_id = firstItem.Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, firstItem, user);
    }

    /// <summary>
    /// Resolve a username string to a Jellyfin user using fuzzy matching.
    /// First tries exact match, then falls back to fuzzy matching.
    /// </summary>
    /// <param name="usernameQuery">The username to search for.</param>
    /// <returns>The matched Jellyfin user, or null if no match found.</returns>
    internal JellyfinUser? ResolveUserByName(string usernameQuery)
    {
        // Get all users from the plugin configuration (registered skill users)
        // and resolve their Jellyfin user names
        var candidates = new List<JellyfinUser>();
        foreach (Entities.User pluginUser in _config.Users)
        {
            JellyfinUser? jfUser = _userManager.GetUserById(pluginUser.Id);
            if (jfUser != null)
            {
                candidates.Add(jfUser);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Try exact match first (case-insensitive)
        JellyfinUser? exactMatch = candidates.FirstOrDefault(u =>
            string.Equals(u.Username, usernameQuery, StringComparison.OrdinalIgnoreCase));

        if (exactMatch != null)
        {
            return exactMatch;
        }

        // Fall back to fuzzy match
        return FuzzyMatch(usernameQuery, candidates, u => u.Username, null);
    }
}