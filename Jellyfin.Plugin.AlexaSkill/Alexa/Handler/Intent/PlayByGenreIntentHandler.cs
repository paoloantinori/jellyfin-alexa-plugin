using System;
using System.Collections.Generic;
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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayByGenreIntent requests. Plays media filtered by genre.
/// </summary>
public class PlayByGenreIntentHandler : BaseHandler
{
    private const int MaxQueryResults = 500;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayByGenreIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayByGenreIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayByGenre, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific genre.
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

        string? genreSlot = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("genre", out Slot? genreSlotObj))
        {
            genreSlot = genreSlotObj.Value;
        }

        if (string.IsNullOrWhiteSpace(genreSlot))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchGenre", locale));
        }

        // JF-466 (JF-467 convention): the payoff is genre MUSIC through the audio
        // stream URL, so the global music flag gates the whole entry. Without the
        // gate, FilterByContentAccess would hand the genre query an EMPTY
        // IncludeItemTypes, which Jellyfin reads as "all kinds" (verified at
        // 10.11.11): a genre shared with movies (e.g. "Action") would queue and
        // play non-audio items. Placed after the slot prompt, before the warming
        // gate and the searching announcement, so a disabled request issues no
        // library query at all.
        SkillResponse? genreMusicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
        if (genreMusicDisabled != null)
        {
            return genreMusicDisabled;
        }

        // Layer-1 gate (GuardIndexReady): the genre queries hit the same cold database
        // the artist index loading proxies (JF-463 wiring); gate before the
        // announcement, same placement as PlayMoodMusic.
        GuardIndexReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

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
            Genres = new[] { genreSlot },
            IncludeItemTypes = FilterByContentAccess(new[] { BaseItemKind.Audio }),
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetGenreItems", cancellationToken).ConfigureAwait(false);

        if (items.Count == 0)
        {
            // JF-463: the genre slot is free-text (AMAZON.Genre in 16 locales,
            // AMAZON.SearchQuery in it-IT), so bare verb+title utterances
            // ("Reproduce abbey road", es) can land here with a title captured as the
            // genre. Mirror the PlayMoodMusic recovery: try the shared cross-media
            // artist fallback (word-count guard + threshold inside); on a miss fall
            // through to the genre not-found unchanged.
            SkillResponse? artistFallback = await TryEntityFallbackAsync(
                genreSlot, jellyfinUser!, user, session, context, locale,
                _libraryManager, _userDataManager, _queueManager, _artistIndex,
                "PlayByGenre artist fallback", cancellationToken).ConfigureAwait(false);
            if (artistFallback != null)
            {
                return artistFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundGenre", locale, genreSlot));
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < items.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = items[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = items[0];

        string itemId = items[0].Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, items[0], user, context);
    }
}
