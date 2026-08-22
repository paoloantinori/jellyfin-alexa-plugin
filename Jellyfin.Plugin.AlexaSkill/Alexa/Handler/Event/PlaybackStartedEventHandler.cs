using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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
    private readonly ILibraryManager? _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackStartedEventHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="libraryManager">Optional library manager for PreEnqueueOnStart item lookups.</param>
    public PlaybackStartedEventHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        ILibraryManager? libraryManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        AudioPlayerRequest? audioPlayerRequest = request as AudioPlayerRequest;
        return audioPlayerRequest != null && audioPlayerRequest.AudioRequestType == AudioRequestType.PlaybackStarted;
    }

    /// <summary>
    /// Set the currently started media as playing, and (when PreEnqueueOnStart is on)
    /// pre-compute the next track's resolution so PlaybackNearlyFinished can respond
    /// instantly when it fires (JF-390). NOTE: Amazon REJECTS AudioPlayer.Play
    /// directives in PlaybackStarted responses ("must not contain more than 0
    /// AudioPlayer.Play directive(s) for this request type"), so we can only
    /// PRE-COMPUTE here, not pre-enqueue. The pre-computed data is stored in
    /// <see cref="NextTrackPrecomputeCache"/> and consumed by
    /// PlaybackNearlyFinishedEventHandler.
    /// </summary>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        AudioPlayerRequest req = (AudioPlayerRequest)request;

        Logger.LogDebug(
            "PlaybackStarted: item={Token}, offset={OffsetMs}ms, sessionId={SessionId}",
            req.Token, req.OffsetInMilliseconds, session.Id);

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

        await SessionManager.OnPlaybackStart(playbackStartInfo).ConfigureAwait(false);

        // JF-390 PreEnqueueOnStart (pre-compute): resolve the next track EARLY so
        // PlaybackNearlyFinished can respond with zero library lookups. This reduces
        // the server-side processing window on high-latency endpoints (Tailscale).
        // We cannot send AudioPlayer.Play from PlaybackStarted (platform rejects it);
        // we only cache the resolution result.
        if (_config.PreEnqueueOnStart && _libraryManager != null)
        {
            TryPrecomputeNext(req.Token, session, user, context);
        }

        // JF-299: return a keep-alive ack (shouldEndSession=null). Amazon REJECTS
        // shouldEndSession=false on AudioPlayer event responses ("Response may not
        // have shouldEndSession set to false"), which raised InvalidResponse (and
        // "Qualcosa è andato storto") on every playback since commit 122d1fb. Audio
        // control intents (Pause/Resume/Next/Previous/Stop) are auto-routed by the
        // platform while audio plays, independent of this ack. See CLAUDE.md
        // "AudioPlayer event restrictions" and the feedback_should_end_session note.
        return BuildKeepAliveResponse();
    }

    /// <summary>
    /// Resolves the next sequential queue item, fetches its library metadata, and
    /// stores the result in <see cref="NextTrackPrecomputeCache"/> keyed by device +
    /// current track token. PlaybackNearlyFinishedEventHandler checks this cache first
    /// and, on a hit, skips the library lookups entirely (instant response).
    /// Deliberately sequential-only: shuffle, repeat, and radio/PostPlay resolution
    /// remain in NearlyFinished as the authoritative resolver.
    /// </summary>
    private void TryPrecomputeNext(string currentToken, SessionInfo session, Entities.User user, Context context)
    {
        if (session.NowPlayingQueue.Count == 0 || !Guid.TryParse(currentToken, out Guid currentId))
        {
            return;
        }

        int currentIndex = -1;
        for (int i = 0; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == currentId)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0 || currentIndex + 1 >= session.NowPlayingQueue.Count)
        {
            return;
        }

        Guid nextId = session.NowPlayingQueue[currentIndex + 1].Id;
        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager?.GetItemById(nextId);
        if (item == null)
        {
            return;
        }

        string streamUrl = GetStreamUrl(nextId.ToString(), user);
        string deviceId = context.System?.Device?.DeviceID ?? string.Empty;

        NextTrackPrecomputeCache.Store(deviceId, currentToken, nextId, item, streamUrl);
        Logger.LogInformation(
            "PlaybackStarted: pre-computed next track='{NextItem}' for device={DeviceId} (current='{CurrentToken}', queue position {Position}/{QueueSize})",
            item.Name, deviceId, currentToken, currentIndex + 2, session.NowPlayingQueue.Count);
    }
}
