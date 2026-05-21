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

    /// <summary>
    /// Initializes a new instance of the <see cref="StartOverIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public StartOverIntentHandler(
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
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        BaseItem? item = session?.FullNowPlayingItem;

        if (session == null)
        {
            return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        // Resolve the Jellyfin user for progress clearing
        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return Task.FromResult<SkillResponse>(userError);
        }

        // If nothing currently playing, try to find last played item with progress
        if (item == null)
        {
            BaseItemKind[] contentTypes = FilterByContentAccess(new[] { BaseItemKind.Audio, BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.AudioBook });
            var (resumeItem, _) = FindLastPlayedItemWithProgress(jellyfinUser!, _libraryManager, _userDataManager, user, contentTypes);

            if (resumeItem == null)
            {
                return Task.FromResult<SkillResponse>(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaToRestart", locale)));
            }

            item = resumeItem;
            Logger.LogInformation("StartOver: found last-played item {ItemName} ({ItemId})", item.Name, item.Id);
        }

        // Clear server-side progress so the item plays from the beginning
        UserItemData? userData = _userDataManager.GetUserData(jellyfinUser!, item);
        if (userData != null)
        {
            userData.PlaybackPositionTicks = 0;
            _userDataManager.SaveUserData(jellyfinUser!, item, userData, UserDataSaveReason.PlaybackProgress, CancellationToken.None);
        }

        string itemId = item.Id.ToString();

        // Use VideoApp for movies/episodes, AudioPlayer for audio/audiobooks
        if (item is MediaBrowser.Controller.Entities.Movies.Movie
            or MediaBrowser.Controller.Entities.TV.Episode)
        {
            return Task.FromResult<SkillResponse>(new SkillResponse
            {
                Version = "1.0",
                Response = new ResponseBody
                {
                    OutputSpeech = new PlainTextOutputSpeech(ResponseStrings.Get("RestartingContent", locale, item.Name)),
                    Directives = new List<IDirective>
                    {
                        new Directive.VideoAppLaunchDirective
                        {
                            VideoItem = new Directive.VideoItem
                            {
                                Source = GetVideoStreamUrl(itemId, user),
                                Metadata = new Directive.VideoItemMetadata { Title = item.Name }
                            }
                        }
                    }
                }
            });
        }

        return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId, item, user, context));
    }
}
