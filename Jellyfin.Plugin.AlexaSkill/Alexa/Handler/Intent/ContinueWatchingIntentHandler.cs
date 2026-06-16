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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ContinueWatchingIntent. Resumes the last in-progress media item.
/// </summary>
public class ContinueWatchingIntentHandler : BaseHandler
{
    private const int MaxCandidates = 50;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinueWatchingIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ContinueWatchingIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ContinueWatching, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resume the last in-progress media item.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        Logger.LogDebug("ContinueWatching: entered, locale={Locale}", locale);

        // Fire-and-forget: never block the handler response on this best-effort "searching…" ping.
        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return Task.FromResult(userError);
        }

        BaseItemKind[] contentTypes = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode });
        var (resumeItem, resumeTicks) = FindLastPlayedItemWithProgress(
            jellyfinUser!, _libraryManager, _userDataManager, user, contentTypes, Logger, MaxCandidates);

        if (resumeItem == null)
        {
            Logger.LogDebug("ContinueWatching: no in-progress item found, returning Tell");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("NoContinueWatching", locale)));
        }

        Logger.LogDebug("ContinueWatching: found item '{ItemName}' ({ItemId}), resumeTicks={ResumeTicks}", resumeItem.Name, resumeItem.Id, resumeTicks);
        string itemId = resumeItem.Id.ToString();
        int offsetMs = (int)TimeSpan.FromTicks(resumeTicks).TotalMilliseconds;

        // Use VideoApp for movies/episodes, AudioPlayer for audio
        if (resumeItem is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode)
        {
            return Task.FromResult(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("NowPlayingWithPosition", locale, resumeItem.Name, FormatPosition(resumeTicks))),
                    Directives = new List<IDirective>
                    {
                        new Directive.VideoAppLaunchDirective
                        {
                            VideoItem = new Directive.VideoItem
                            {
                                Source = GetVideoStreamUrl(itemId, user),
                                Metadata = new Directive.VideoItemMetadata { Title = resumeItem.Name }
                            }
                        }
                    }
                }
            });
        }

        return Task.FromResult(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, resumeItem, user, context, offsetMs));
    }
}
