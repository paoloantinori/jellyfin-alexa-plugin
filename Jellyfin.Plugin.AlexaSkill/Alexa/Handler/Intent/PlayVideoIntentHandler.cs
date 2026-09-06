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
/// Handler for PlayVideoIntent — searches for movies and episodes by title
/// and launches video playback via the Alexa VideoApp interface.
/// </summary>
public class PlayVideoIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    public PlayVideoIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayVideo, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.VideoPlaybackEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? titleQuery = intentRequest.Intent.Slots?.TryGetValue("title", out var slot) == true ? slot.Value : null;

        // JF-509: the AMAZON.SearchQuery title has no catalog to anchor its fill boundary,
        // and the statistical fill drifts between builds: 'voglio guardare il film ada'
        // filled title='film ada' on 2026-09-06 (device corr=655db7c3; the same utterance
        // filled 'ada' the day before). The strip is RAW-FIRST like the album calling-word
        // strip (JF-469): a movie genuinely titled 'Film Stars Don't Die in Liverpool' must
        // stay findable by its raw title, so the raw query runs first and the stripped
        // value is retried only on a confirmed miss (see the search below).

        if (string.IsNullOrWhiteSpace(titleQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchVideoTitle", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var videoSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = titleQuery,
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(videoSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> videos = await RetryAsync(
            () => _libraryManager.GetItemList(videoSearchQuery),
            "GetVideos",
            cancellationToken).ConfigureAwait(false);

        if (videos.Count == 0)
        {
            // JF-509 raw-first retry, BEFORE the fuzzy fallback: the fill may have
            // swallowed the carrier noun ('film ada' for 'ada'), and the stripped
            // exact query is a far higher-probability hit than a fuzzy near-miss on
            // the raw value (live: the fuzzy answered 'Intendevi <unrelated>?' while
            // 'ada' existed). A title genuinely starting with the noun was already
            // found by the raw query above.
            string? strippedTitle = StripLeadingMediaNoun(titleQuery);
            if (strippedTitle != null)
            {
                Logger.LogDebug("PlayVideo: raw title miss, retrying stripped '{Stripped}' (JF-509)", strippedTitle);
                var strippedQuery = new InternalItemsQuery
                {
                    User = jellyfinUser,
                    Recursive = true,
                    SearchTerm = strippedTitle,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(strippedQuery, user, _libraryManager);
                videos = await RetryAsync(
                    () => _libraryManager.GetItemList(strippedQuery),
                    "GetVideosStrippedTitle",
                    cancellationToken).ConfigureAwait(false);
            }

            if (videos.Count == 0)
            {
                // When a stripped title exists the fuzzy runs on the STRIPPED value:
                // the swallowed-carrier shape ('film ada') fuzzies against 'ada', the
                // title the user meant, not against the raw 'film ada' (live 19:38:
                // raw-fuzzy matched 'Cicada' score 50 while 'Ada: My Mother the
                // Architect' existed and fuzzy-on-'ada' is the high-confidence hit).
                string fuzzyQuery = strippedTitle ?? titleQuery;
                var fuzzy = await SearchItemsFuzzyAsync(fuzzyQuery, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.Movie, BaseItemKind.Episode }, cancellationToken, "PlayVideoFuzzyFallback").ConfigureAwait(false);
                if (fuzzy != null)
                {
                    videos = new List<BaseItem> { fuzzy.Value.Item };
                }
                else
                {
                    return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundVideo", locale, titleQuery));
                }
            }
        }

        if (videos.Count > 1)
        {
            BaseItem? videoMatch = null;
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                titleQuery,
                videos,
                v => v.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeVideo,
                locale,
                best =>
                {
                    videoMatch = best;
                    return null!;
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                if (missResponse != null)
                {
                    return missResponse;
                }

                videos = new List<BaseItem> { videoMatch! };
            }
            else
            {
                var matches = videos.Take(3).Select(v => (v.Id, v.Name, (string?)GetImageUrl(v.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeVideo, locale, context);
            }
        }

        BaseItem video = videos[0];

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = video.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = video;

        // Check for existing playback progress and announce resume position.
        // Note: Alexa VideoApp does not support seek/offset natively, so the video
        // will start from the beginning, but we inform the user where they left off.
        UserItemData? userData = _userDataManager.GetUserData(jellyfinUser!, video);
        long resumeTicks = userData?.PlaybackPositionTicks ?? 0;
        if (resumeTicks > 0)
        {
            Logger.LogInformation("PlayVideo: resuming {Title} from {Position}", video.Name, FormatPosition(resumeTicks));
        }

        // Alexa VideoApp does not support seek/offset natively (the video starts from the
        // beginning); the announce only informs the user where they left off.
        // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
        return BuildVideoAppLaunchResponse(
            context,
            locale,
            GetVideoAppLaunchUrl(video, user),
            video.Name,
            BuildVideoLaunchSpeech(video, locale, resumeTicks, GetAnnounceNowPlaying(user)));
    }

    /// <summary>
    /// Media nouns (+ definite/indefinite articles) the movie-title fill can swallow,
    /// per language family of the 17 locales (JF-509). The strip is prefix-only and
    /// repeated (fill can include 'il film', 'film', 'un film', 'la película', ...).
    /// Every entry carries its trailing space implicitly via the match below, so
    /// word-fragment titles ('filmstar') never match.
    /// </summary>
    private static readonly string[] LeadingMediaNounPrefixes =
    {
        // it
        "il film", "un film", "il movie", "un movie", "film", "movie",
        // en
        "the movie", "a movie", "movie",
        // de
        "der film", "den film", "ein film", "film",
        // es
        "la película", "una película", "película", "la pelicula", "pelicula",
        // fr
        "le film", "un film",
        // pt
        "o filme", "um filme", "filme",
        // nl
        "de film", "een film",
        // hi / ar / ja are handled by their own carriers being non-latinate; their
        // models carry the noun inside the sample carrier so the fill does not split it
    };

    /// <summary>
    /// Strips a leading localized media noun (with articles) that the statistical
    /// fill swallowed into the title (JF-509; the SearchQuery title has no catalog
    /// to anchor its boundary). Language-agnostic like StripSongCarrierPhrase.
    /// Returns null when the value does not change or is only the noun/article:
    /// the raw-first caller retries only on a real change.
    /// </summary>
    /// <param name="title">The raw title slot value.</param>
    /// <returns>The cleaned title, or null when there is nothing new to retry.</returns>
    private static string? StripLeadingMediaNoun(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        string cleaned = title.Trim();
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (string prefix in LeadingMediaNounPrefixes)
            {
                bool withSpace = cleaned.Length > prefix.Length
                    && cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && char.IsWhiteSpace(cleaned[prefix.Length]);
                if (withSpace)
                {
                    cleaned = cleaned[prefix.Length..].TrimStart();
                    changed = true;
                    break;
                }

                // The whole value is the noun/article: nothing searchable remains.
                if (string.Equals(cleaned, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }
        }

        return cleaned;
    }
}
