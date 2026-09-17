using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayNextEpisodeIntent (JF-324): "play the next episode of {series}"
/// and "continue watching {series}" share the NextUp core in
/// <see cref="TvNextUpService.PlayNextUpEpisodeAsync"/> (the per-user next unwatched
/// or in-progress episode, with the JF-324 DateCreated fallback when the series is
/// fully watched). JF-583: the episode_position slot distinguishes the "latest
/// episode of {series}" phrasing, which resolves by RECENCY in
/// <see cref="TvNextUpService.PlayLatestEpisodeAsync"/> (PremiereDate desc, no watch
/// filter) because NextUp answers watch state in library order, never date. An
/// empty, absent, or unresolved position keeps NextUp: the historical default.
/// </summary>
public class PlayNextEpisodeIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ITVSeriesManager _tvSeriesManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayNextEpisodeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="tvSeriesManager">Instance of the <see cref="ITVSeriesManager"/> interface (NextUp source).</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlayNextEpisodeIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ITVSeriesManager tvSeriesManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _tvSeriesManager = tvSeriesManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayNextEpisode, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.VideoPlaybackEnabled, request) is { } disabled)
        {
            Logger.LogDebug("PlayNextEpisode: feature disabled (VideoPlaybackEnabled), returning disabled response");
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? seriesName = intentRequest.Intent.Slots?.TryGetValue("series_name", out var seriesSlot) == true ? seriesSlot.Value : null;

        // JF-550 (dead-mic sweep; JF-549 class): the empty-slot prompt elicits
        // with the mic open, and a captured cancel word ends the flow.
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "PlayNextEpisode") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(seriesName))
        {
            return BuildDialogElicitResponse("DidNotCatchSeriesName", locale, "series_name", IntentNames.PlayNextEpisode, "series_name", "episode_position");
        }

        // Media-type gate after the slot prompt and before any query (the JF-467
        // placement idiom): a videos-disabled configuration must not reach NextUp.
        if (IfMediaTypeDisabled(c => c.VideosEnabled, request) is { } mediaDisabled)
        {
            return mediaDisabled;
        }

        // JF-583: the episode_position slot decides the semantics ('next'/empty
        // keeps NextUp; 'latest' resolves by recency). Entity resolution decides
        // when it matched, the localized word table when it did not.
        bool latestRequested = Util.EpisodePosition.IsLatest(
            intentRequest.Intent.Slots?.TryGetValue("episode_position", out var positionSlot) == true ? positionSlot : null,
            locale);

        Logger.LogDebug("PlayNextEpisode: seriesName='{SeriesName}', locale={Locale}, latestRequested={LatestRequested}", seriesName, locale, latestRequested);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var (series, seriesError) = await TvNextUp.ResolveSeriesForPlaybackAsync(_libraryManager, jellyfinUser!, user, seriesName, locale, cancellationToken).ConfigureAwait(false);
        if (seriesError != null || series is null)
        {
            return seriesError!;
        }

        if (latestRequested)
        {
            return await TvNextUp.PlayLatestEpisodeAsync(_libraryManager, _userDataManager, jellyfinUser!, user, session, series, locale, context, request, cancellationToken).ConfigureAwait(false);
        }

        return await TvNextUp.PlayNextUpEpisodeAsync(_tvSeriesManager, _libraryManager, _userDataManager, jellyfinUser!, user, session, series, locale, context, request, cancellationToken).ConfigureAwait(false);
    }
}
