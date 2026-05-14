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
/// Handler for PlayMoodMusicIntent requests. Plays music matching a mood by mapping
/// mood keywords to genres and querying Audio items.
/// </summary>
public class PlayMoodMusicIntentHandler : BaseHandler
{
    private static readonly Dictionary<string, string[]> MoodGenreMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["relaxing"] = new[] { "ambient", "acoustic", "jazz", "classical", "new age" },
        ["chill"] = new[] { "chillout", "ambient", "lounge", "downtempo" },
        ["upbeat"] = new[] { "pop", "rock", "dance", "electronic" },
        ["energetic"] = new[] { "rock", "electronic", "metal", "punk" },
        ["focus"] = new[] { "classical", "ambient", "instrumental" },
        ["romantic"] = new[] { "r&b", "jazz", "soul", "pop" },
        ["happy"] = new[] { "pop", "dance", "reggae" },
        ["sad"] = new[] { "blues", "indie", "folk", "alternative" },
        ["party"] = new[] { "dance", "electronic", "hip hop", "pop" },
        ["workout"] = new[] { "electronic", "rock", "hip hop", "metal" },
        ["morning"] = new[] { "acoustic", "pop", "indie", "folk", "jazz" },
        ["evening"] = new[] { "jazz", "ambient", "classical", "lounge", "soul" },
        ["dinner"] = new[] { "jazz", "classical", "acoustic", "bossa nova", "soul" }
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayMoodMusicIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayMoodMusicIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayMoodMusic, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play music matching the requested mood.
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

        string? mood = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("mood", out Slot? moodSlot))
        {
            mood = moodSlot.Value;
        }

        if (string.IsNullOrEmpty(mood))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchMood", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        string[] genres = ResolveGenres(mood, DateTime.Now.Hour);

        List<BaseItem> foundItems = new();

        foreach (string genre in genres)
        {
            var query = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Genres = new[] { genre },
                Limit = 100,
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(query, user, _libraryManager);

            IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), "GetMoodItems", cancellationToken).ConfigureAwait(false);

            if (items.Count > 0)
            {
                foundItems.AddRange(items);
                break;
            }
        }

        if (foundItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundMood", locale, mood));
        }

        BaseItem selected = foundItems[Random.Shared.Next(foundItems.Count)];

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new() { Id = selected.Id }
        };

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = selected;

        string itemId = selected.Id.ToString();

        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, selected, user, context);
    }

    /// <summary>
    /// Resolves a mood string to an array of genre names.
    /// Tries an exact match first, then a contains match, then falls back to
    /// using the mood word itself as a genre. Reorders genres based on time of day.
    /// </summary>
    /// <param name="mood">The mood keyword from the user.</param>
    /// <param name="hour">Current hour (0-23) for time-of-day bias.</param>
    /// <returns>An array of genre names to search for.</returns>
    internal static string[] ResolveGenres(string mood, int hour = -1)
    {
        string[]? genres = null;

        if (MoodGenreMap.TryGetValue(mood, out string[]? mapped))
        {
            genres = mapped;
        }
        else
        {
            foreach (KeyValuePair<string, string[]> entry in MoodGenreMap)
            {
                if (mood.Contains(entry.Key, StringComparison.OrdinalIgnoreCase))
                {
                    genres = entry.Value;
                    break;
                }
            }
        }

        if (genres == null)
        {
            return new[] { mood };
        }

        if (hour < 0)
        {
            return genres;
        }

        // Bias genre order toward time-appropriate genres
        string[] preferred = GetTimePreferredGenres(hour);
        return genres
            .OrderByDescending(g => preferred.Contains(g, StringComparer.OrdinalIgnoreCase))
            .ThenBy(_ => Random.Shared.Next())
            .ToArray();
    }

    private static string[] GetTimePreferredGenres(int hour) => hour switch
    {
        >= 5 and < 10 => new[] { "acoustic", "pop", "folk", "indie", "classical" },
        >= 10 and < 14 => new[] { "pop", "rock", "indie", "alternative" },
        >= 14 and < 18 => new[] { "rock", "electronic", "hip hop", "dance" },
        >= 18 and < 22 => new[] { "jazz", "ambient", "lounge", "soul", "classical" },
        _ => new[] { "ambient", "chillout", "classical", "jazz" }
    };
}
