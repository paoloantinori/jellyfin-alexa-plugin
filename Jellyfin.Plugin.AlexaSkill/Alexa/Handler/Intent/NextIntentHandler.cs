using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.NextIntent intents and next directive.
/// </summary>
public class NextIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="NextIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="queueManager">Optional per-device queue manager (the last-played ledger the JF-564 medium classification reads).</param>
    public NextIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonNext, System.StringComparison.Ordinal)) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Next);
    }

    /// <summary>
    /// Play the next item in the queue.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A play directive of the next item in the queue or empty response if the queue is empty.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("NextIntent: entered, queueSize={QueueSize}, nowPlaying={NowPlayingId}", session.NowPlayingQueue.Count, session.FullNowPlayingItem?.Id);

        // JF-564: during a VideoApp-family medium the queue logic below must not run
        // (the shared refusal helper owns the rationale); an empty ledger (Unknown)
        // keeps the music semantics unchanged.
        PlaybackLaunchBuilder.PlayingMedium medium = Launch.ResolvePlayingMedium(context, _libraryManager, _queueManager);
        if (PlaybackLaunchBuilder.BuildVideoAppTransportRefusal(medium, GetLocale(request)) is { } refusal)
        {
            Logger.LogDebug("NextIntent: {Medium} playing, answered by the transport refusal", medium);
            return Task.FromResult(refusal);
        }

        // JF-577: repair a restart/re-registration-wiped session queue before the
        // queue read (rationale on the shared helper): a coherent persisted device
        // queue turns the false "no more tracks" below into the real queue.
        bool rehydrated = ProgressReporter.TryRehydrateSessionQueueFromDevice(
            _queueManager, session, context, Logger, "NextIntent");
        System.Guid? currentItemId = ProgressReporter.ResolveCurrentItemId(session, context, rehydrated);

        // check if we have any media in the queue and the is currently something playing
        if (session.NowPlayingQueue.Count == 0 || currentItemId == null)
        {
            Logger.LogDebug("NextIntent: empty queue or no now-playing item, returning Empty");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        }

        // get the next item in the queue, skip last item
        int idx = SessionQueue.IndexOfQueueItem(session, currentItemId.Value);
        if (idx >= 0 && idx < session.NowPlayingQueue.Count - 1)
        {
            System.Guid nextItemId = session.NowPlayingQueue[idx + 1].Id;
            {
                string item_id = session.NowPlayingQueue[idx + 1].Id.ToString();
                BaseItem? nextItem = _libraryManager.GetItemById(nextItemId);
                if (nextItem == null)
                {
                    Logger.LogDebug("NextIntent: next item {ItemId} not found in library, returning Empty", nextItemId);
                    return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
                }

                session.FullNowPlayingItem = nextItem;

                Logger.LogDebug("NextIntent: playing next item '{ItemName}' ({ItemId})", nextItem.Name, nextItemId);
                // JF-507: codec-gated audio-launch decision; an EAC3-family video item in
                // the queue routes to the audio-only transcode instead of dying on the raw
                // static bytes (JF-505 does not apply: this launch is audio-shaped).
                AudioLaunchSource source = Launch.ResolveAudioLaunchSource(nextItem, item_id, user, 0);
                return Task.FromResult<SkillResponse>(Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, source, item_id, nextItem, user, context));
            }
        }

        Logger.LogDebug("NextIntent: already at last item in queue, returning Empty");
        return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
    }
}