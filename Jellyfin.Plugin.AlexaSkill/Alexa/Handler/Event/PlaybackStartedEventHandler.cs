using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaybackStarted events.
/// </summary>
#pragma warning disable CA1711
public class PlaybackStartedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public PlaybackStartedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackStarted;
    }

    /// <summary>
    /// Set the currently started media as playing.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>Empty response.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;

        long startTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks;
        PlaybackStartInfo playbackStartInfo = new PlaybackStartInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PlaybackOrder = session.PlayState.PlaybackOrder,
            RepeatMode = session.PlayState.RepeatMode,
            PositionTicks = startTicks,
            PlaybackStartTimeTicks = startTicks,
        };
        SessionManager.OnPlaybackStart(playbackStartInfo).ConfigureAwait(false);

        return ResponseBuilder.Empty();
    }
}