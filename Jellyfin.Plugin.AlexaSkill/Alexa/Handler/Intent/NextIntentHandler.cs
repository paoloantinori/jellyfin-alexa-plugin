using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
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
        => Task.FromResult(Progress.ServeAdjacentQueueItem(
            _queueManager,
            _libraryManager,
            session,
            context,
            user,
            GetLocale(request),
            ProgressReporter.AdjacentQueueDirection.Next,
            "NextIntent"));
}
