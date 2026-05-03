using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ClearQueueIntent requests.
/// Removes all items from the playback queue except the currently playing item.
/// </summary>
public class ClearQueueIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClearQueueIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ClearQueueIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ClearQueue, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        // Keep only the currently playing item in the queue
        if (session.FullNowPlayingItem != null)
        {
            session.NowPlayingQueue = new List<QueueItem>
            {
                new() { Id = session.FullNowPlayingItem.Id }
            };
        }
        else
        {
            session.NowPlayingQueue = new List<QueueItem>();
        }

        Logger.LogInformation("ClearQueueIntent: queue cleared");
        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("QueueCleared", locale)));
    }
}
