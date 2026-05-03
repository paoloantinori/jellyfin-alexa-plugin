using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ListQueueIntent requests.
/// Reports the upcoming items in the playback queue.
/// </summary>
public class ListQueueIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListQueueIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ListQueueIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.ListQueue, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        if (session.NowPlayingQueue.Count == 0)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("QueueEmpty", locale)));
        }

        // Find the current playing item index, list items after it
        int currentIndex = -1;
        if (session.FullNowPlayingItem != null)
        {
            for (int i = 0; i < session.NowPlayingQueue.Count; i++)
            {
                if (session.NowPlayingQueue[i].Id == session.FullNowPlayingItem.Id)
                {
                    currentIndex = i;
                    break;
                }
            }
        }

        var upcoming = session.NowPlayingQueue.Skip(currentIndex + 1).Take(5).ToList();
        if (upcoming.Count == 0)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("QueueEmpty", locale)));
        }

        var names = new StringBuilder();
        for (int i = 0; i < upcoming.Count; i++)
        {
            var item = _libraryManager.GetItemById(upcoming[i].Id);
            if (item != null)
            {
                if (names.Length > 0)
                {
                    names.Append(", ");
                }

                names.Append(item.Name);
            }
        }

        if (names.Length == 0)
        {
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("QueueEmpty", locale)));
        }

        return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("QueueList", locale, names.ToString())));
    }
}
