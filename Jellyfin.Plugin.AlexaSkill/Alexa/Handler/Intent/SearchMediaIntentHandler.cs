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
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using MediaType = Jellyfin.Data.Enums.MediaType;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for SearchMediaIntent — unified search across all Jellyfin content types.
/// </summary>
public class SearchMediaIntentHandler : BaseHandler
{
    private static readonly BaseItemKind[] _playableTypes =
    [
        BaseItemKind.Audio,
        BaseItemKind.MusicAlbum,
        BaseItemKind.Movie,
        BaseItemKind.Episode,
        BaseItemKind.Series,
        BaseItemKind.Playlist,
        BaseItemKind.AudioBook
    ];

    private const int ArtistFallbackThreshold = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;

    public SearchMediaIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.SearchMedia, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? query = intentRequest.Intent.Slots?.TryGetValue("query", out var slot) == true
            ? slot.Value
            : null;

        if (string.IsNullOrWhiteSpace(query))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("CouldNotUnderstand", locale));
        }

        await SendProgressiveResponse(
            context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        IReadOnlyList<BaseItem> results = await SearchWithAsrFallbackAsync(query,
            searchTerm =>
            {
                var q = new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    SearchTerm = searchTerm,
                    IncludeItemTypes = FilterByContentAccess(_playableTypes),
                    Limit = Plugin.Instance?.Configuration?.MaxSearchResults ?? 20,
                    OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(q, user, _libraryManager);
                return RetryAsync(() => _libraryManager.GetItemList(q), "UnifiedSearch", cancellationToken);
            }).ConfigureAwait(false);

        if (results.Count <= ArtistFallbackThreshold)
        {
            IReadOnlyList<BaseItem> artistResults = await SearchByArtistNameAsync(query, jellyfinUser!, user, cancellationToken).ConfigureAwait(false);
            if (artistResults.Count > 0)
            {
                Logger.LogInformation("Artist fallback for '{Query}': found {Count} items via artist lookup", query, artistResults.Count);
                results = results.Concat(artistResults).ToList();
            }
        }

        if (results.Count == 0)
        {
            Logger.LogInformation("Search for '{Query}' returned no results", query);
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        var deduped = results.GroupBy(i => i.Id).Select(g => g.First()).ToList();
        Logger.LogInformation("Search for '{Query}' returned {Count} deduplicated results", query, deduped.Count);

        if (deduped.Count == 1)
        {
            Logger.LogInformation("Single result — auto-playing '{Item}'", deduped[0].Name);
            return PlayItem(deduped[0], user, session, context);
        }

        // Disambiguation uses MediaTypeSong; YesIntentHandler will play matches as audio.
        // Mixed-type results (audio + video) are rare for search disambiguation.
        BaseItem? topMatch = FuzzyMatch(query, deduped, i => i.Name, user);
        if (topMatch != null)
        {
            Logger.LogInformation("Fuzzy match hit '{Item}' — auto-playing", topMatch.Name);
            return PlayItem(topMatch, user, session, context);
        }

        var (missOutcome, missResponse) = HandleFuzzyMiss(
            query,
            deduped,
            i => i.Name,
            best => new List<(Guid, string)> { (best.Id, FormatWithTypeLabel(best)) },
            DisambiguationHelper.MediaTypeSong,
            locale,
            best => PlayItem(best, user, session, context),
            user: user);

        if (missOutcome != FuzzyMissOutcome.NotFound)
        {
            Logger.LogInformation("Fuzzy miss outcome: {Outcome}", missOutcome);
            return missResponse!;
        }

        var topItems = deduped.Take(3).ToList();
        var matches = topItems.Select(i => (i.Id, FormatWithTypeLabel(i), (string?)GetImageUrl(i.Id.ToString("N"), user))).ToList();
        Logger.LogInformation("Disambiguating top {Count} items: {Items}", topItems.Count, string.Join(", ", topItems.Select(i => i.Name)));
        SkillResponse response = DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);

        return response;
    }

    private async Task<IReadOnlyList<BaseItem>> SearchByArtistNameAsync(
        string query,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
            query, user, _libraryManager, _artistIndex, Logger,
            (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "ArtistLookup", ct),
            cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        var artistItemsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            MediaTypes = new[] { MediaType.Audio },
            ArtistIds = new[] { artists[0].Id },
            IncludeItemTypes = FilterByContentAccess(_playableTypes),
            Limit = Plugin.Instance?.Configuration?.MaxSearchResults ?? 20,
            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(artistItemsQuery, user, _libraryManager);

        return await RetryAsync(
            () => _libraryManager.GetItemList(artistItemsQuery),
            "ArtistItemsLookup",
            cancellationToken).ConfigureAwait(false);
    }

    private SkillResponse PlayItem(
        BaseItem item, Entities.User user, SessionInfo session, Context context)
    {
        string itemId = item.Id.ToString();

        List<QueueItem> queueItems = new()
        {
            new QueueItem { Id = item.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = item;

        if (IsVideoType(item))
        {
            return new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    ShouldEndSession = true,
                    Directives = new List<IDirective>
                    {
                        new VideoAppLaunchDirective
                        {
                            VideoItem = new VideoItem
                            {
                                Source = GetStreamUrl(itemId, user),
                                Metadata = new VideoItemMetadata
                                {
                                    Title = item.Name
                                }
                            }
                        }
                    }
                }
            };
        }

        return BuildAudioPlayerResponse(
            global::Alexa.NET.Response.Directive.PlayBehavior.ReplaceAll,
            GetStreamUrl(itemId, user),
            itemId,
            item,
            user,
            context);
    }

    private static bool IsVideoType(BaseItem item)
    {
        return item is global::MediaBrowser.Controller.Entities.Movies.Movie
            || (item is global::MediaBrowser.Controller.Entities.TV.Episode ep
                && ep.MediaType == MediaType.Video);
    }

    private static string GetTypeName(BaseItem item)
    {
        if (item is Audio)
        {
            return "song";
        }

        if (item is MusicAlbum)
        {
            return "album";
        }

        if (item is global::MediaBrowser.Controller.Entities.Movies.Movie)
        {
            return "movie";
        }

        if (item is global::MediaBrowser.Controller.Entities.TV.Episode)
        {
            return "episode";
        }

        if (item is global::MediaBrowser.Controller.Entities.TV.Series series)
        {
            return series.MediaType == MediaType.Audio ? "podcast" : "series";
        }

        // Concrete AudioBook type is in the server assembly, not the controller package
        if (item.GetType().Name.Equals("AudioBook", StringComparison.Ordinal))
        {
            return "audiobook";
        }

        var runtimeName = item.GetType().Name;
        if (runtimeName.Contains("Playlist", StringComparison.Ordinal))
        {
            return "playlist";
        }

        return "media";
    }

    private static string FormatWithTypeLabel(BaseItem item)
    {
        return $"{GetTypeName(item)}: {item.Name}";
    }
}
