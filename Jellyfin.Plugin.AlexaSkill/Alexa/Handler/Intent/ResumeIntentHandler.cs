using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Data;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for AMAZON.ResumeIntent intents and resume directive.
/// </summary>
public class ResumeIntentHandler : BaseHandler
{
    private ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResumeIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="dbRepo">The database repository instance.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public ResumeIntentHandler(
        ISessionManager sessionManager,
        DbRepo dbRepo,
        ILibraryManager libraryManager,
        ILoggerFactory loggerFactory) : base(sessionManager, dbRepo, loggerFactory)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "AMAZON.ResumeIntent", System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Pause any currently playing media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>Emptry skill response.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        if (session.FullNowPlayingItem == null)
        {
            return ResponseBuilder.Tell("There is no media currently playing.");
        }

        string item_id = session.FullNowPlayingItem.Id.ToString();

        return ResponseBuilder.AudioPlayerPlay(PlayBehavior.Enqueue, GetStreamUrl(item_id, user), item_id);
    }
}