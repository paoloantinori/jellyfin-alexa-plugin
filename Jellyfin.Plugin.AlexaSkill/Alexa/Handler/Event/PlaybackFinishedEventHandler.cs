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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

#pragma warning disable CA1711
public class PlaybackFinishedEventHandler : BaseHandler
#pragma warning restore CA1711
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlaybackFinishedEventHandler(
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
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackFinished;
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;

        Logger.LogDebug(
            "PlaybackFinished: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

        PlaybackStopInfo playbackStopInfo = new PlaybackStopInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(req.Token),
            PositionTicks = TimeSpan.FromMilliseconds(req.OffsetInMilliseconds).Ticks,
        };
        await SessionManager.OnPlaybackStopped(playbackStopInfo).ConfigureAwait(false);

        Logger.LogDebug(
            "PlaybackFinished: saved to server — item={Token}, positionTicks={Ticks}",
            req.Token, playbackStopInfo.PositionTicks);

        // If PlaybackNearlyFinished enqueued a next track, keep the session alive
        // for APL touch events and the upcoming track.
        bool hasQueuedNext = context.AudioPlayer?.PlayerActivity == "PLAYING"
            || context.AudioPlayer?.PlayerActivity == "BUFFER_UNDERRUN";

        if (!hasQueuedNext)
        {
            string locale = GetLocale(request);
            string deviceId = context.System.Device.DeviceID;

            // Queue is exhausted — check PostPlay state before ending session
            if (PostPlayState.TryGet(session.UserId, deviceId, out var postPlayMode, out var postPlayItemId))
            {
                if (postPlayMode == PostPlayBehavior.AutoPlay)
                {
                    return await HandleAutoPlay(postPlayItemId!, session, user, context, locale, cancellationToken).ConfigureAwait(false);
                }

                if (postPlayMode == PostPlayBehavior.Ask)
                {
                    return HandleAsk(locale);
                }
            }

            Logger.LogInformation("PlaybackFinished: queue exhausted, ending session to dismiss APL screen");
            return BuildEndSessionResponse();
        }

        return BuildKeepAliveResponse();
    }

    /// <summary>
    /// Handle AutoPlay: find similar tracks, announce, and start playing.
    /// Enables RadioModeState for subsequent gapless transitions.
    /// </summary>
    private async Task<SkillResponse> HandleAutoPlay(
        string itemId,
        SessionInfo session,
        Entities.User user,
        Context context,
        string locale,
        CancellationToken cancellationToken)
    {
        PostPlayState.Remove(session.UserId, context.System.Device.DeviceID);

        SkillResponse? response = await TryBuildPostPlayResponseAsync(
            itemId, session, user, context, locale,
            _libraryManager, _userManager, cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            return BuildEndSessionResponse();
        }

        // Add artist announcement to the AutoPlay response
        var currentAudio = _libraryManager.GetItemById(Guid.Parse(itemId)) as MediaBrowser.Controller.Entities.Audio.Audio;
        string artistName = currentAudio?.Artists?.FirstOrDefault() ?? currentAudio?.AlbumEntity?.Name ?? string.Empty;
        string announcement = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            ResponseStrings.Get("PostPlayAutoPlayAnnouncement", locale),
            artistName);

        response.Response.OutputSpeech = new SsmlOutputSpeech($"<speak>{EscapeXml(announcement)}</speak>");
        return response;
    }

    /// <summary>
    /// Handle Ask mode: prompt the user with a yes/no question.
    /// State remains for Yes/No handlers to consume.
    /// </summary>
    private SkillResponse HandleAsk(string locale)
    {
        string prompt = ResponseStrings.Get("PostPlayAskPrompt", locale);
        string reprompt = ResponseStrings.Get("PostPlayAskReprompt", locale);

        return ResponseBuilder.Ask(prompt, new Reprompt(reprompt));
    }
}
