using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.PreviousIntent intents and previous directive.
/// </summary>
public class PreviousIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreviousIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public PreviousIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        PlaybackControllerRequest? playbackControllerRequest = request as PlaybackControllerRequest;
        return (intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.AmazonPrevious, System.StringComparison.Ordinal)) ||
            (playbackControllerRequest != null && playbackControllerRequest.PlaybackRequestType is PlaybackControllerRequestType.Previous);
    }

    /// <summary>
    /// Play the previous item in the queue.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A play directive of the previous item in the queue or empty response if the queue is empty.</returns>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        Logger.LogDebug("PreviousIntent: entered, queueSize={QueueSize}, nowPlaying={NowPlayingId}", session.NowPlayingQueue.Count, session.FullNowPlayingItem?.Id);

        // check if we have any media in the queue and there is currently something playing
        if (session.NowPlayingQueue.Count == 0 || session.FullNowPlayingItem == null)
        {
            Logger.LogDebug("PreviousIntent: empty queue or no now-playing item, returning Empty");
            return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
        }

        // get the previous item in the queue
        for (int i = 1; i < session.NowPlayingQueue.Count; i++)
        {
            if (session.NowPlayingQueue[i].Id == session.FullNowPlayingItem.Id)
            {
                System.Guid prevItemId = session.NowPlayingQueue[i - 1].Id;
                string item_id = session.NowPlayingQueue[i - 1].Id.ToString();
                BaseItem? prevItem = _libraryManager.GetItemById(prevItemId);
                if (prevItem == null)
                {
                    Logger.LogDebug("PreviousIntent: previous item {ItemId} not found in library, returning Empty", prevItemId);
                    return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
                }

                string previousItemId = session.NowPlayingQueue[i - 1].Id.ToString();
                session.FullNowPlayingItem = prevItem;

                Logger.LogDebug("PreviousIntent: playing previous item '{ItemName}' ({ItemId})", prevItem.Name, prevItemId);
                return Task.FromResult<SkillResponse>(BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, prevItem, user, context));
            }
        }

        Logger.LogDebug("PreviousIntent: already at first item in queue, returning Empty");
        return Task.FromResult<SkillResponse>(ResponseBuilder.Empty());
    }
}