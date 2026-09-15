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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.StartOverIntent intents.
/// Restarts the currently playing item or the last-played item with progress when nothing is playing.
/// </summary>
public class StartOverIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILiveTvStreamResolver _streamResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartOverIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="streamResolver">The live-TV stream resolver (PlaybackInfo URL for channel rejoins).</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public StartOverIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILiveTvStreamResolver streamResolver,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _streamResolver = streamResolver;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonStartOver, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restart the currently playing media, or the last-played item with progress when nothing is playing.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Skill response with playback directive or error message.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        BaseItem? item = session?.FullNowPlayingItem;

        Logger.LogDebug("StartOver: entered, hasNowPlaying={HasNowPlaying}", item != null);

        if (session == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
        }

        // Resolve the Jellyfin user for progress clearing
        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // If nothing currently playing, try to find last played item with progress
        if (item == null)
        {
            BaseItemKind[] contentTypes = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.AudioBook });
            var (resumeItem, _) = FindLastPlayedItemWithProgress(jellyfinUser!, _libraryManager, _userDataManager, user, contentTypes);

            if (resumeItem == null)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaToRestart", locale));
            }

            item = resumeItem;
            Logger.LogInformation("StartOver: found last-played item {ItemName} ({ItemId})", item.Name, item.Id);
        }

        // Live TV (JF-564): a "restart" of live TV is a rejoin of the live stream.
        // The static /Audio/{id}/stream endpoint the audio branch below would use
        // returns HTTP 500 for a live source, so the channel rides the SAME launch
        // block PlayChannel uses: ILiveTvStreamResolver resolution + VideoApp.Launch,
        // with the resolver's not-available Tell when the stream cannot be resolved.
        // Sits above the progress clear: a live source has no resume position, so the
        // channel must not pay the user-data read/write.
        if (item is MediaBrowser.Controller.LiveTv.LiveTvChannel)
        {
            if (IfFeatureDisabled(c => c.LiveTvEnabled, request) is { } liveTvDisabled)
            {
                return liveTvDisabled;
            }

            return await Launch.BuildChannelLaunchResponseAsync(
                _streamResolver, item, context, request, user, session, locale, cancellationToken).ConfigureAwait(false);
        }

        // Clear server-side progress so the item plays from the beginning
        UserItemData? userData = _userDataManager.GetUserData(jellyfinUser!, item);
        if (userData != null)
        {
            userData.PlaybackPositionTicks = 0;
            _userDataManager.SaveUserData(jellyfinUser!, item, userData, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
        }

        string itemId = item.Id.ToString();

        // NativeControlsForBooks (JF-563): restart a book from 0 through the same VideoApp
        // HLS entry PlayBook uses. BuildVideoAppAudioResponse routes a multi-chapter book
        // to the concat endpoint and a single-chapter one to the per-item endpoint whose
        // controller re-mints the chapter-scoped token, so no URL is hand-built here.
        // Gated at the caller, NOT in IsVideoAppLaunchItem: the predicate is the durable
        // movie-shaped kind list (its other callers must not treat a book as video) and
        // the flag is mutable config (the JF-499 W1 split; see ResumeIntentHandler).
        if (item is MediaBrowser.Controller.Entities.AudioBook)
        {
            // The tracker's high-water mark never decreases, so the stale position must be
            // dropped here or the next resume would jump back near where the user just
            // restarted from. Cleared regardless of the flag (the tracker is only READ
            // under it), so a flag-off restart cannot leave a stale mark behind either.
            Plugin.Instance?.AudiobookPositionTracker?.Clear(ResumeMath.GetAudiobookBookKey(item));

            if (Plugin.Instance?.Configuration?.NativeControlsForBooks == true)
            {
                SkillResponse response = Launch.BuildVideoAppAudioResponse(itemId, item, user, context: context);

                // JF-501: the restart announce rides the progressive vehicle on a VideoApp
                // launch (same as the movie branch); a screenless device degrades to
                // AudioPlayer and the announce stays on the final response.
                response.Response.OutputSpeech = await Launch.SpeakVideoLaunchAnnounceAsync(
                    context,
                    request,
                    new PlainTextOutputSpeech(ResponseStrings.Get("RestartingContent", locale, item.Name))).ConfigureAwait(false);
                return response;
            }
        }

        // Use VideoApp for movies/episodes, AudioPlayer for audio/audiobooks
        if (item is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode)
        {
            // JF-498 codec-routed source; JF-505 screenless-device gate (shared launch builder).
            // JF-501: the announce is spoken progressively (directive-only final response).
            return await Launch.BuildVideoAppLaunchResponseAsync(
                context,
                request,
                locale,
                Launch.GetVideoAppLaunchUrl(item, user),
                item.Name,
                new PlainTextOutputSpeech(ResponseStrings.Get("RestartingContent", locale, item.Name))).ConfigureAwait(false);
        }

        return Launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, item, user, context);
    }
}
