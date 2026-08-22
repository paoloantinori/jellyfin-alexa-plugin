using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
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
    /// enqueue the next track from the server-side queue so the device transitions
    /// automatically without needing the timing-sensitive PlaybackNearlyFinished event.
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

        // JF-390 PreEnqueueOnStart: enqueue the next track when this one STARTS,
        // eliminating the per-track PlaybackNearlyFinished round-trip dependency.
        if (_config.PreEnqueueOnStart && _libraryManager != null)
        {
            SkillResponse? enqueueResponse = TryPreEnqueueNext(req.Token, session, user, context);
            if (enqueueResponse != null)
            {
                return enqueueResponse;
            }
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
    /// Resolves the next sequential item in the session queue after the currently
    /// playing token and returns an AudioPlayer.Play (Enqueue) response for it.
    /// Returns null (caller falls through to keep-alive) when there is no next item,
    /// the queue is empty, or the current item cannot be found. Deliberately simple
    /// (sequential only): shuffle, repeat-one, radio mode, and PostPlay AutoPlay are
    /// still handled by PlaybackNearlyFinished, which remains the authoritative
    /// resolver for those modes.
    /// </summary>
    private SkillResponse? TryPreEnqueueNext(string currentToken, SessionInfo session, Entities.User user, Context context)
    {
        if (session.NowPlayingQueue.Count == 0 || !Guid.TryParse(currentToken, out Guid currentId))
        {
            return null;
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
            return null;
        }

        Guid nextId = session.NowPlayingQueue[currentIndex + 1].Id;
        MediaBrowser.Controller.Entities.BaseItem? item = _libraryManager?.GetItemById(nextId);
        if (item == null)
        {
            return null;
        }

        Logger.LogInformation(
            "PlaybackStarted: PreEnqueueOnStart enqueuing next item='{NextItem}' after current='{CurrentItem}' (queue position {Position}/{QueueSize})",
            item.Name, currentToken, currentIndex + 2, session.NowPlayingQueue.Count);

        return BuildAudioPlayerResponse(PlayBehavior.Enqueue, GetStreamUrl(nextId.ToString(), user), nextId.ToString(), item, user, context);
    }
}
