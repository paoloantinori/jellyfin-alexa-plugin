using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.PauseIntent, AMAZON.StopIntent and AMAZON.CancelIntent intents and pause directive.
/// </summary>
public class PauseIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PauseIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public PauseIntentHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && ((string.Equals(intentRequest.Intent.Name, IntentNames.AmazonPause, System.StringComparison.Ordinal) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonStop, System.StringComparison.Ordinal)) ||
            string.Equals(intentRequest.Intent.Name, IntentNames.AmazonCancel, System.StringComparison.Ordinal))) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Pause);
    }

    /// <summary>
    /// Pause or stop currently playing media.
    /// All paths send AudioPlayer.Stop to guarantee audio stops on device.
    /// Stop/Cancel ends the session. Pause keeps it open for resume and announces position.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A task representing the async operation.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        bool isStopOrCancel = request is IntentRequest ir &&
            (string.Equals(ir.Intent.Name, IntentNames.AmazonStop, System.StringComparison.Ordinal) ||
             string.Equals(ir.Intent.Name, IntentNames.AmazonCancel, System.StringComparison.Ordinal));

        Logger.LogDebug(
            "PauseIntent: isStopOrCancel={IsStop}, device={DeviceId}, audioPlayer token={Token} activity={Activity} offset={OffsetMs}ms",
            isStopOrCancel,
            context.System.Device.DeviceID,
            context.AudioPlayer?.Token,
            context.AudioPlayer?.PlayerActivity,
            context.AudioPlayer?.OffsetInMilliseconds);

        if (session?.FullNowPlayingItem != null)
        {
            Logger.LogDebug(
                "PauseIntent: nowPlaying={ItemName} ({ItemId}), playState position={PositionTicks} ticks",
                session.FullNowPlayingItem.Name,
                session.FullNowPlayingItem.Id,
                session.PlayState?.PositionTicks);
        }
        else
        {
            Logger.LogDebug("PauseIntent: no nowPlayingItem in session");
        }

        // Both pause and stop need AudioPlayer.Stop directive to guarantee audio stops.
        // Stop/Cancel ends the session; Pause keeps it open for resume.
        var response = BuildPauseResponse();

        if (isStopOrCancel)
        {
            response.Response.ShouldEndSession = true;
            return Task.FromResult(response);
        }

        // Pause: announce position and add visual card when seek is enabled
        if (Plugin.Instance?.Configuration?.SeekEnabled == true && session?.NowPlayingItem != null)
        {
            string locale = GetLocale(request);
            string positionText = BuildPositionDisplay(session, locale);
            if (!string.IsNullOrEmpty(positionText))
            {
                response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = positionText };
                response.Response.Card = new StandardCard
                {
                    Title = session.NowPlayingItem.Name ?? ResponseStrings.Get("NowPlayingCardTitle", locale),
                    Content = positionText
                };
            }
        }

        return Task.FromResult(response);
    }
}
