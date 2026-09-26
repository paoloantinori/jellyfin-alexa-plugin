using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

public class LoopOnIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopOnIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">The library manager, for the shared current-item resolver and medium classification (JF-635).</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    /// <param name="queueManager">Optional per-device queue manager (the last-played ledger the resolver and the JF-632 medium gate read).</param>
    public LoopOnIntentHandler(
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
        // Locale aliases (LoopAllOn vs the AMAZON.LoopOnIntent built-in): see the
        // comment on the loop intent constants in IntentNames.
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null
            && (string.Equals(intentRequest.Intent.Name, IntentNames.AmazonLoopOn, StringComparison.Ordinal)
                || string.Equals(intentRequest.Intent.Name, IntentNames.LoopAllOn, StringComparison.Ordinal));
    }

    /// <summary>
    /// Applies repeat-all to the currently playing item; shared body in
    /// <see cref="ProgressReporter.ApplyRepeatModeAsync"/>.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>The spoken repeat-mode confirmation Tell, the honest refusal Tell over a VideoApp-routed medium, or the no-media tell when nothing is playing.</returns>
    /// Ordering note (JF-447): this progress write is AWAITED inside its own request
    /// path, so a later stop cannot overtake it mid-write; that is why loop toggles are
    /// exempt from PlaybackReportOrdering registration (unlike the fire-and-forget
    /// playback-start reports, JF-425). Do not move it to a fire-and-forget task
    /// without registering it there.
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Progress.ApplyRepeatModeAsync(request, context, session, RepeatMode.RepeatAll, "LoopOn", _libraryManager, _queueManager);
}
