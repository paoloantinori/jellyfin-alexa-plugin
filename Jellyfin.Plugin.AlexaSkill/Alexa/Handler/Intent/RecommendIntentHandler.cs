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
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
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
/// Handler for RecommendIntent requests. Recommends media based on the user's
/// listening/watching history by finding top genres from played items, then
/// suggesting unplayed items from those genres.
/// </summary>
public class RecommendIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public RecommendIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "RecommendIntent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Recommend and play media based on the user's listening/watching history.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response with a recommended item or a no-results message.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.RecommendationsEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? mediaType = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("media_type", out Slot? mediaSlot))
        {
            mediaType = mediaSlot.Value;
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        BaseItemKind[] itemTypes = FilterByContentAccess(GetItemTypes(mediaType));

        // Step 1: Get user's most-played items to find their top genres
        var historyQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = itemTypes,
            IsPlayed = true,
            Limit = 20,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(historyQuery, user);

        IReadOnlyList<BaseItem> playedItems = await RetryAsync(() => _libraryManager.GetItemList(historyQuery), "GetPlayedItems", cancellationToken).ConfigureAwait(false);

        // Step 2: Collect distinct genres from played items
        List<string> genres = playedItems
            .SelectMany(i => i.Genres)
            .Distinct()
            .Take(3)
            .ToList();

        IReadOnlyList<BaseItem> recommendations;

        if (genres.Count > 0)
        {
            // Step 3: Query for unplayed items in those genres
            var recQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = itemTypes,
                Genres = genres.ToArray(),
                IsPlayed = false,
                Limit = Plugin.Instance?.Configuration?.MaxRecommendationResults ?? 10,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(recQuery, user);

            recommendations = await RetryAsync(() => _libraryManager.GetItemList(recQuery), "GetGenreRecommendations", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            recommendations = Array.Empty<BaseItem>();
        }

        // Fallback: try recent unplayed items if no genre-based recommendations
        if (recommendations.Count == 0)
        {
            var fallbackQuery = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = itemTypes,
                IsPlayed = false,
                Limit = Plugin.Instance?.Configuration?.MaxRecommendationResults ?? 10,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(fallbackQuery, user);

            recommendations = await RetryAsync(() => _libraryManager.GetItemList(fallbackQuery), "GetFallbackRecommendations", cancellationToken).ConfigureAwait(false);
        }

        if (recommendations.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoRecommendations", locale));
        }

        // Step 4: Pick a random item from results
        BaseItem item = recommendations[Random.Shared.Next(recommendations.Count)];
        string itemId = item.Id.ToString();

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = item.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = item;

        // Use VideoApp for movies, AudioPlayer for audio
        if (item is MediaBrowser.Controller.Entities.Movies.Movie)
        {
            string? recSsml = GetSsml("RecommendPlayingSsml", locale, item.Name);
            var outputSpeech = recSsml != null
                ? (IOutputSpeech)new SsmlOutputSpeech { Ssml = $"<speak>{recSsml}</speak>" }
                : new PlainTextOutputSpeech(ResponseStrings.Get("RecommendPlaying", locale, item.Name));

            return new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
                    OutputSpeech = outputSpeech,
                    Directives = new List<IDirective>
                    {
                        new VideoAppLaunchDirective
                        {
                            VideoItem = new Directive.VideoItem
                            {
                                Source = GetStreamUrl(itemId, user),
                                Metadata = new Directive.VideoItemMetadata { Title = item.Name }
                            }
                        }
                    }
                }
            };
        }

        // For audio, add NowPlaying speech before the audio directive
        string? nowPlayingSsml = GetSsml("NowPlayingSsml", locale, item.Name);
        var audioResponse = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, item, user);
        if (nowPlayingSsml != null)
        {
            audioResponse.Response.OutputSpeech = new SsmlOutputSpeech { Ssml = $"<speak>{nowPlayingSsml}</speak>" };
        }

        return audioResponse;
    }

    private static BaseItemKind[] GetItemTypes(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return new[] { BaseItemKind.Audio, BaseItemKind.Movie };
        }

        return mediaType.ToLowerInvariant() switch
        {
            "music" => new[] { BaseItemKind.Audio },
            "movie" => new[] { BaseItemKind.Movie },
            "video" => new[] { BaseItemKind.Movie },
            _ => new[] { BaseItemKind.Audio, BaseItemKind.Movie }
        };
    }
}
